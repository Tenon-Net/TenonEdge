using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Runtime.Diagnostics;

namespace TenonEdge.UnitTests.Runtime;

public sealed class InputBufferBudgetTests
{
    [Fact]
    public async Task FullRawPartitionStillAllowsFullParsingPartitionAndSharedStatusNeverExceedsTotal()
    {
        var budget = new InFlightBudget(64L * 1024 * 1024, 8L * 1024 * 1024);
        using var raw = budget.TryReserveBuffer(budget.BufferCapacity);
        Assert.NotNull(raw);
        Assert.Null(budget.TryReserveBuffer(1));
        var channel = new BudgetedInputChannel(1, budget) { OutputReservationBytes = 60L * 1024 * 1024 };
        var invoked = false;
        await ((InputMessageWriter)channel.Writer).ProduceAsync(48L * 1024 * 1024, 48L * 1024 * 1024, _ =>
        {
            invoked = true;
            Assert.Equal(budget.Capacity, budget.Status.ReservedBytes);
            return ValueTask.FromResult<EdgeMessage?>(null);
        }, TestContext.Current.CancellationToken);
        Assert.True(invoked);
        Assert.Equal(budget.BufferCapacity, budget.Status.ReservedBytes);
        raw.Dispose();
        Assert.Equal(0, budget.Status.ReservedBytes);
        Assert.Equal(budget.Capacity, budget.Status.PeakReservedBytes);
    }

    [Fact]
    public async Task WorkingWaitAndCancellationCannotReleaseRawOwnership()
    {
        var budget = new InFlightBudget(100, 20);
        using var raw = budget.TryReserveBuffer(20);
        Assert.NotNull(raw);
        Assert.True(budget.TryReserve(80));
        using var cancellation = new CancellationTokenSource();
        var wait = budget.ReserveAsync(1, cancellation.Token).AsTask();
        Assert.False(wait.IsCompleted);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
        Assert.Equal(100, budget.Status.ReservedBytes);
        budget.Release(80);
        Assert.Equal(20, budget.Status.ReservedBytes);
        Assert.Throws<InvalidOperationException>(() => budget.Release(1));
        raw.Dispose();
        Assert.Equal(0, budget.Status.ReservedBytes);
    }

    [Fact]
    public void LeasesAreIndependentAcrossGenerationsAndDisposeIsConcurrentAndIdempotent()
    {
        var budget = new InFlightBudget(100, 20);
        var old = budget.TryReserveBuffer(10);
        using var current = budget.TryReserveBuffer(10);
        Assert.NotNull(old);
        Assert.NotNull(current);
        Parallel.For(0, 1000, _ => old.Dispose());
        Assert.Equal(10, budget.Status.ReservedBytes);
        using var replacement = budget.TryReserveBuffer(10);
        Assert.NotNull(replacement);
        old.Dispose();
        Assert.Equal(20, budget.Status.ReservedBytes);
        Assert.Null(budget.TryReserveBuffer(1));
    }

    [Fact]
    public void ConcurrentAdmissionsCannotOverbookSharedPartition()
    {
        var budget = new InFlightBudget(1000, 100);
        var leases = new System.Collections.Concurrent.ConcurrentBag<IDisposable>();
        Parallel.For(0, 1000, _ =>
        {
            if (budget.TryReserveBuffer(10) is { } lease)
            { leases.Add(lease); }
        });
        Assert.Equal(10, leases.Count);
        Assert.Equal(100, budget.Status.ReservedBytes);
        Parallel.ForEach(leases, lease => lease.Dispose());
        Assert.Equal(0, budget.Status.ReservedBytes);
        Assert.Equal(100, budget.Status.PeakReservedBytes);
    }

    [Fact]
    public async Task OversizedWorkingReservationFailsInsteadOfWaitingOnUnavailablePartition()
    {
        var budget = new InFlightBudget(100, 20);
        Assert.False(budget.TryReserve(81));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await budget.ReserveAsync(81, TestContext.Current.CancellationToken));
        var writer = (InputMessageWriter)new BudgetedInputChannel(1, budget).Writer;
        await Assert.ThrowsAsync<InputMessageSizeException>(async () => await writer.ProduceAsync(81,
            _ => throw new InvalidOperationException("不能执行解析。"), TestContext.Current.CancellationToken));
        Assert.False(budget.TryReserve(100)); // 原始分区空闲也不借给等待解析路径。
        Assert.Equal(0, budget.Status.ReservedBytes);
    }

    [Fact]
    public async Task ActualOutputCannotBorrowTheRawPartitionAfterParsing()
    {
        var budget = new InFlightBudget(2500, 500);
        using var raw = budget.TryReserveBuffer(500);
        var channel = new BudgetedInputChannel(1, budget) { OutputReservationBytes = 1800 };
        var message = new EdgeMessage(Guid.NewGuid(), "input", "device", DateTimeOffset.UnixEpoch,
            TimeSource.Gateway, DateTimeOffset.UnixEpoch, new Dictionary<string, FieldValue> { ["value"] = FieldValue.FromString("测试") });
        Assert.True(channel.Charge(message) > budget.WorkingCapacity);
        await Assert.ThrowsAsync<InputMessageSizeException>(async () => await ((InputMessageWriter)channel.Writer).ProduceAsync(2000, 2000,
            _ => ValueTask.FromResult<EdgeMessage?>(message), TestContext.Current.CancellationToken));
        Assert.False(channel.Reader.TryRead(out _));
        Assert.Equal(500, budget.Status.ReservedBytes);
    }

    [Fact]
    public async Task BufferWaitCancellationDoesNotStealLeaseAndReleaseWakesAnotherConnection()
    {
        var budget = new InFlightBudget(100, 20);
        using var first = budget.TryReserveBuffer(20);
        using var cancel = new CancellationTokenSource();
        var canceled = budget.ReserveBufferAsync(20, cancel.Token).AsTask();
        var next = budget.ReserveBufferAsync(20, TestContext.Current.CancellationToken).AsTask();
        Assert.False(next.IsCompleted);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
        Assert.Equal(20, budget.Status.ReservedBytes);
        first!.Dispose();
        using var second = await next.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(20, budget.Status.ReservedBytes);
        first.Dispose();
        Assert.Equal(20, budget.Status.ReservedBytes);
    }

    [Fact]
    public void InvalidPartitionsAndReservationsFailWithoutChangingAccounting()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new InFlightBudget(100, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new InFlightBudget(100, 100));
        var budget = new InFlightBudget(100, 20);
        Assert.Throws<ArgumentOutOfRangeException>(() => budget.TryReserveBuffer(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => budget.TryReserveBuffer(-1));
        Assert.Null(budget.TryReserveBuffer(long.MaxValue));
        Assert.Null(new InFlightBudget(100).TryReserveBuffer(1));
        Assert.Equal(0, budget.Status.ReservedBytes);
    }
}
