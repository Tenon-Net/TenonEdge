using System.Collections.Concurrent;
using TenonEdge.Runtime.Diagnostics;

namespace TenonEdge.UnitTests.Runtime;

public sealed class PreviewOperationBudgetTests
{
    [Fact]
    public void DefaultsEnforceBothLimitsAndReleaseOnlyTheirOwnSession()
    {
        var budget = new PreviewOperationBudget();
        using var a1 = Assert.IsAssignableFrom<IDisposable>(budget.TryAcquire("a"));
        using var a2 = Assert.IsAssignableFrom<IDisposable>(budget.TryAcquire("a"));
        Assert.Null(budget.TryAcquire("a"));
        using var b1 = Assert.IsAssignableFrom<IDisposable>(budget.TryAcquire("b"));
        using var c1 = Assert.IsAssignableFrom<IDisposable>(budget.TryAcquire("c"));
        Assert.Null(budget.TryAcquire("d"));
        b1.Dispose();
        Assert.Null(budget.TryAcquire("a"));
        using var c2 = Assert.IsAssignableFrom<IDisposable>(budget.TryAcquire("c"));
        a1.Dispose();
        using var a3 = Assert.IsAssignableFrom<IDisposable>(budget.TryAcquire("a"));
        Assert.Null(budget.TryAcquire("d"));
    }

    [Fact]
    public void SessionKeysAreOrdinalAndReleasedSessionsDoNotConsumeCapacity()
    {
        var budget = new PreviewOperationBudget(2, 1);
        using var lower = Assert.IsAssignableFrom<IDisposable>(budget.TryAcquire("a"));
        using var upper = Assert.IsAssignableFrom<IDisposable>(budget.TryAcquire("A"));
        upper.Dispose();
        for (var index = 0; index < 10000; index++)
        {
            using var lease = budget.TryAcquire($"session-{index}");
            Assert.NotNull(lease);
        }
        Assert.Null(budget.TryAcquire("a"));
        using var again = budget.TryAcquire("A");
        Assert.NotNull(again);
    }

    [Fact]
    public void ConcurrentAdmissionAndRepeatedDisposalCannotOverbook()
    {
        var budget = new PreviewOperationBudget();
        for (var round = 0; round < 20; round++)
        {
            var admitted = new ConcurrentBag<(string Session, IDisposable Lease)>();
            Parallel.For(0, 100, index =>
            {
                var session = $"session-{index % 3}";
                if (budget.TryAcquire(session) is { } lease)
                { admitted.Add((session, lease)); }
            });
            Assert.Equal(4, admitted.Count);
            Assert.All(admitted.GroupBy(item => item.Session), group => Assert.InRange(group.Count(), 1, 2));
            Parallel.ForEach(admitted.SelectMany(item => Enumerable.Repeat(item.Lease, 10)), lease => lease.Dispose());
        }
    }

    [Fact]
    public async Task CanceledWaitDoesNotReleaseAnOperationStillCleaningUp()
    {
        var budget = new PreviewOperationBudget(1, 1);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = HoldUntilFinishedAsync();
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.WaitAsync(canceled.Token));
        Assert.Null(budget.TryAcquire("another-session"));
        finish.SetResult();
        await operation;
        using var next = budget.TryAcquire("another-session");
        Assert.NotNull(next);

        async Task HoldUntilFinishedAsync()
        {
            using var lease = budget.TryAcquire("session");
            Assert.NotNull(lease);
            await finish.Task;
        }
    }

    [Fact]
    public void LiveLimitsPreserveExistingLeasesAndInvalidProvidersNeverAdmit()
    {
        var limits = new PreviewOperationLimits(4, 2);
        var budget = new PreviewOperationBudget(readLimits: () => limits);
        using var one = budget.TryAcquire("a");
        using var two = budget.TryAcquire("a");
        Assert.NotNull(one);
        Assert.NotNull(two);
        limits = new(1, 1);
        Assert.Null(budget.TryAcquire("b"));
        one.Dispose();
        one.Dispose();
        Assert.Null(budget.TryAcquire("b"));
        limits = new(0, 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => budget.TryAcquire("b"));
        limits = new(4, 2);
        using var additional = budget.TryAcquire("a");
        Assert.NotNull(additional);
        Assert.Null(budget.TryAcquire("a"));
    }

    [Fact]
    public void InvalidLimitsAndSessionIdsAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PreviewOperationBudget(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PreviewOperationBudget(sessionLimit: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PreviewOperationBudget(1, 2));
        var budget = new PreviewOperationBudget();
        Assert.Throws<ArgumentNullException>(() => budget.TryAcquire(null!));
        Assert.Throws<ArgumentException>(() => budget.TryAcquire(""));
        Assert.Throws<ArgumentException>(() => budget.TryAcquire(" "));
    }
}
