using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Runtime.Diagnostics;

namespace TenonEdge.UnitTests.Runtime;

/// <summary>只验证内存在途预约的所有权，不把预算释放当作 SQLite 持久接管。</summary>
public sealed class InputFormattingRetentionTests
{
    [Fact]
    public void RepeatedRetentionDoesNotReleaseAnotherOwnersBytes()
    {
        var budget = new InFlightBudget(1_000_000);
        var channel = new BudgetedInputChannel(2, budget) { OutputReservationBytes = 100_000 };
        var first = Message();
        var second = Message();
        Assert.True(channel.Writer.TryWrite(first));
        Assert.True(channel.Writer.TryWrite(second));
        var secondCharge = channel.Charge(second);
        channel.RetainAfterFormatting(first, 4096);
        var retained = budget.Status.ReservedBytes;
        Assert.Equal(secondCharge + first.BudgetBytes + 4096, retained);

        channel.RetainAfterFormatting(first, 1);
        channel.RetainAfterFormatting(first, 200_000);
        Assert.Equal(retained, budget.Status.ReservedBytes);
        channel.Release(first);
        Assert.Equal(secondCharge, budget.Status.ReservedBytes);
        channel.Release(second);
        Assert.Equal(0, budget.Status.ReservedBytes);
    }

    [Fact]
    public void LargeFrozenEstimateNeverExpandsOrPrematurelyReleasesOriginalReservation()
    {
        var budget = new InFlightBudget(1_000_000);
        var channel = new BudgetedInputChannel(1, budget) { OutputReservationBytes = 100_000 };
        var message = Message();
        Assert.True(channel.Writer.TryWrite(message));
        var original = budget.Status.ReservedBytes;
        channel.RetainAfterFormatting(message, 200_000);
        Assert.Equal(original, budget.Status.ReservedBytes);
        Assert.Throws<ArgumentOutOfRangeException>(() => channel.RetainAfterFormatting(message, -1));
        Assert.Equal(original, budget.Status.ReservedBytes);
        channel.Release(message);
        Assert.Equal(0, budget.Status.ReservedBytes);
    }

    [Fact]
    public void ReducingConfiguredBudgetDoesNotRevokeRetainedOwnership()
    {
        var budget = new InFlightBudget(1_000_000);
        var channel = new BudgetedInputChannel(1, budget) { OutputReservationBytes = 100_000 };
        var message = Message();
        Assert.True(channel.Writer.TryWrite(message));
        channel.RetainAfterFormatting(message, 4096);
        var retained = budget.Status.ReservedBytes;
        budget.ConfigureCapacity(1);
        Assert.Equal(retained, budget.Status.ReservedBytes);
        Assert.False(budget.TryReserve(1));
        channel.Release(message);
        Assert.Equal(0, budget.Status.ReservedBytes);
        Assert.True(budget.TryReserve(1));
        budget.Release(1);
    }

    [Fact]
    public void RetentionEntryIsRemovedWhenTheOriginalOwnerReleases()
    {
        var budget = new InFlightBudget(1_000_000);
        var channel = new BudgetedInputChannel(1, budget) { OutputReservationBytes = 100_000 };
        var message = Message();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            Assert.True(channel.Writer.TryWrite(message));
            Assert.True(channel.HoldingReader.TryRead(out var received));
            Assert.Same(message, received);
            Assert.Equal(channel.Charge(message), budget.Status.ReservedBytes);
            channel.RetainAfterFormatting(message, 4096);
            Assert.Equal(message.BudgetBytes + 4096, budget.Status.ReservedBytes);
            channel.Release(message);
            Assert.Equal(0, budget.Status.ReservedBytes);
        }
    }

    [Theory]
    [InlineData(InputHandoffStatus.Committed)]
    [InlineData(InputHandoffStatus.Rejected)]
    [InlineData(InputHandoffStatus.Stopped)]
    [InlineData(InputHandoffStatus.StorageFailed)]
    public async Task ReturningWorkspaceDoesNotCompleteDurableHandoff(InputHandoffStatus outcome)
    {
        var budget = new InFlightBudget(1_000_000);
        var channel = new BudgetedInputChannel(1, budget) { OutputReservationBytes = 100_000, UsesDurableHandoff = true };
        var message = Message();
        var writer = Assert.IsAssignableFrom<InputMessageWriter>(channel.Writer);
        var handoff = writer.ProduceConfirmedAsync(message.BudgetBytes, message.BudgetBytes,
            _ => ValueTask.FromResult<EdgeMessage?>(message), TestContext.Current.CancellationToken).AsTask();
        Assert.True(channel.HoldingReader.TryRead(out var received));
        Assert.Same(message, received);
        channel.RetainAfterFormatting(message, 4096);
        Assert.False(handoff.IsCompleted);
        Assert.Equal(message.BudgetBytes + 4096 + 1024, budget.Status.ReservedBytes);
        channel.Release(message, outcome);
        Assert.Equal(outcome, await handoff);
        Assert.Equal(0, budget.Status.ReservedBytes);
    }

    private static EdgeMessage Message()
    {
        var now = DateTimeOffset.UtcNow;
        return new(Guid.NewGuid(), "input", "device", now, TimeSource.Gateway, now, []);
    }
}
