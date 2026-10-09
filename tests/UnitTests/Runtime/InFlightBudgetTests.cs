using System.Threading.Channels;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Runtime.Diagnostics;

namespace TenonEdge.UnitTests.Runtime;

public sealed class InFlightBudgetTests
{
    [Fact]
    public void NonWaitingProductionReleasesBudgetOnExceptionsAndExactChargeWorks()
    {
        var message = Message();
        var budget = new InFlightBudget(10000);
        var channel = new BudgetedInputChannel(1, budget);
        var writer = (InputMessageWriter)channel.Writer;
        Assert.Throws<InvalidOperationException>(() => writer.TryProduce(10000, 10000, () => throw new InvalidOperationException(), TestContext.Current.CancellationToken));
        Assert.Equal(0, budget.Status.ReservedBytes);
        Assert.True(writer.TryProduce(message.BudgetBytes, message.BudgetBytes, () => message, TestContext.Current.CancellationToken));
        Assert.Equal(message.BudgetBytes, budget.Status.ReservedBytes);
        Assert.True(channel.Reader.TryRead(out _));
        Assert.Equal(0, budget.Status.ReservedBytes);
        channel.Writer.TryComplete();
        Assert.Throws<ChannelClosedException>(() => writer.TryProduce(10000, 10000, () => message, TestContext.Current.CancellationToken));
        Assert.Equal(0, budget.Status.ReservedBytes);
    }

    [Fact]
    public async Task UndeclaredConfirmationCannotSilentlyWaitWithoutADispatcher()
    {
        var budget = new InFlightBudget(10000);
        var channel = new BudgetedInputChannel(1, budget);
        var invoked = false;
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await ((InputMessageWriter)channel.Writer).ProduceConfirmedAsync(1000, 1000,
            _ => { invoked = true; return ValueTask.FromResult<EdgeMessage?>(null); }, TestContext.Current.CancellationToken));
        Assert.False(invoked);
        Assert.Equal(0, budget.Status.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DurableConfirmationWaitsForConsumerAndCancellationDoesNotReleaseOwnedMessage(bool cancel)
    {
        var budget = new InFlightBudget(10000);
        var channel = new BudgetedInputChannel(1, budget) { UsesDurableHandoff = true };
        var message = Message();
        using var caller = new CancellationTokenSource();
        var receipt = ((InputMessageWriter)channel.Writer).ProduceConfirmedAsync(10000, 10000,
            _ => ValueTask.FromResult<EdgeMessage?>(message), caller.Token).AsTask();
        Assert.True(channel.HoldingReader.TryRead(out var held));
        Assert.False(receipt.IsCompleted);
        Assert.True(channel.HasHandoff(message));
        if (cancel)
        {
            await caller.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => receipt);
            Assert.True(budget.Status.ReservedBytes > 0);
        }
        channel.Release(held!, InputHandoffStatus.Committed);
        if (!cancel)
        { Assert.Equal(InputHandoffStatus.Committed, await receipt); }
        Assert.False(channel.HasHandoff(message));
        Assert.Equal(0, budget.Status.ReservedBytes);
    }

    [Fact]
    public async Task CancelledPublicationIntoFullChannelRemovesConfirmationAndItsBudget()
    {
        var budget = new InFlightBudget(20000);
        var channel = new BudgetedInputChannel(1, budget) { UsesDurableHandoff = true };
        var first = Message();
        Assert.True(channel.Writer.TryWrite(first));
        var second = Message();
        using var caller = new CancellationTokenSource();
        var receipt = ((InputMessageWriter)channel.Writer).ProduceConfirmedAsync(10000, 10000,
            _ => ValueTask.FromResult<EdgeMessage?>(second), caller.Token).AsTask();
        Assert.True(channel.HasHandoff(second));
        await caller.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => receipt);
        Assert.False(channel.HasHandoff(second));
        Assert.Equal(channel.Charge(first), budget.Status.ReservedBytes);
        Assert.True(channel.Reader.TryRead(out _));
        Assert.Equal(0, budget.Status.ReservedBytes);
    }

    [Fact]
    public async Task NullProductionIsExplicitRejectionAndDrainWithoutDispatchNeverConfirms()
    {
        var budget = new InFlightBudget(10000);
        var channel = new BudgetedInputChannel(1, budget) { UsesDurableHandoff = true };
        var writer = (InputMessageWriter)channel.Writer;
        Assert.Equal(InputHandoffStatus.Rejected, await writer.ProduceConfirmedAsync(10000, 10000,
            _ => ValueTask.FromResult<EdgeMessage?>(null), TestContext.Current.CancellationToken));
        Assert.Equal(0, budget.Status.ReservedBytes);
        var receipt = writer.ProduceConfirmedAsync(10000, 10000,
            _ => ValueTask.FromResult<EdgeMessage?>(Message()), TestContext.Current.CancellationToken).AsTask();
        channel.Writer.TryComplete();
        Assert.True(channel.Reader.TryRead(out _));
        Assert.Equal(InputHandoffStatus.Stopped, await receipt);
        Assert.Equal(0, budget.Status.ReservedBytes);
    }

    [Fact]
    public async Task CappedTwoPhaseReservationStillRejectsAnActualOutputPhaseThatCannotFit()
    {
        var budget = new InFlightBudget(2000);
        var channel = new BudgetedInputChannel(1, budget) { OutputReservationBytes = 1800 };
        await Assert.ThrowsAsync<InputMessageSizeException>(async () =>
            await ((InputMessageWriter)channel.Writer).ProduceAsync(2000, 2000,
                _ => ValueTask.FromResult<EdgeMessage?>(Message()), TestContext.Current.CancellationToken));
        Assert.Equal(0, budget.Status.ReservedBytes);
        Assert.False(channel.Reader.TryRead(out _));
    }

    [Theory]
    [InlineData(8)]
    [InlineData(12)]
    public void MappingWorkspaceIsChargedBeforeAcceptingAnInputAndReleasedAfterConsumption(int mebibytes)
    {
        var message = Message();
        var workspace = mebibytes * 1024L * 1024;
        var budget = new InFlightBudget(64L * 1024 * 1024);
        var channel = new BudgetedInputChannel(1, budget) { MappingWorkingReservationBytes = workspace };
        Assert.True(channel.Writer.TryWrite(message));
        Assert.Equal(message.BudgetBytes + workspace, budget.Status.ReservedBytes);
        Assert.True(channel.Reader.TryRead(out _));
        Assert.Equal(0, budget.Status.ReservedBytes);
    }

    [Fact]
    public async Task ConservativeTwoPhaseUpperBoundReservesCapacityAndChecksActualResult()
    {
        var message = Message();
        var budget = new InFlightBudget(64L * 1024 * 1024);
        var channel = new BudgetedInputChannel(1, budget)
        { ReserveSerializationWorkspace = true, MappingWorkingReservationBytes = 8L * 1024 * 1024, OutputReservationBytes = 16L * 1024 * 1024 };
        await ((InputMessageWriter)channel.Writer).ProduceAsync(48L * 1024 * 1024, 20L * 1024 * 1024, _ =>
        {
            Assert.Equal(budget.Capacity, budget.Status.ReservedBytes);
            return ValueTask.FromResult<EdgeMessage?>(message);
        }, TestContext.Current.CancellationToken);
        Assert.True(budget.Status.ReservedBytes < budget.Capacity);
        Assert.True(channel.Reader.TryRead(out _));
        Assert.Equal(0, budget.Status.ReservedBytes);
    }

    [Fact]
    public async Task DistinctWorkAndMessageLimitsReserveTheMaximumOfSequentialPhases()
    {
        var message = Message();
        var budget = new InFlightBudget(48L * 1024 * 1024);
        var channel = new BudgetedInputChannel(1, budget) { ReserveSerializationWorkspace = true, OutputReservationBytes = 1024 };
        await ((InputMessageWriter)channel.Writer).ProduceAsync(budget.Capacity, message.BudgetBytes, _ =>
        {
            Assert.Equal(budget.Capacity, budget.Status.ReservedBytes);
            return ValueTask.FromResult<EdgeMessage?>(message);
        }, TestContext.Current.CancellationToken);
        Assert.Equal(channel.Charge(message), budget.Status.ReservedBytes);
        Assert.True(channel.Reader.TryRead(out _));
        Assert.Equal(0, budget.Status.ReservedBytes);
    }

    [Fact]
    public async Task LargerWorkReservationDoesNotPermitOversizedResultMessages()
    {
        var message = Message();
        var budget = new InFlightBudget(10000);
        var channel = new BudgetedInputChannel(1, budget);
        await Assert.ThrowsAsync<InputMessageSizeException>(async () =>
            await ((InputMessageWriter)channel.Writer).ProduceAsync(10000, 1,
                _ => ValueTask.FromResult<EdgeMessage?>(message), TestContext.Current.CancellationToken));
        Assert.False(channel.Reader.TryRead(out _));
        Assert.Equal(0, budget.Status.ReservedBytes);
    }

    [Fact]
    public async Task WaitToWriteHonorsFailedMessagesBudgetAndWakesOnCompletion()
    {
        var message = Message();
        var budget = new InFlightBudget(message.BudgetBytes);
        var first = new BudgetedInputChannel(1, budget);
        var second = new BudgetedInputChannel(1, budget);
        var token = TestContext.Current.CancellationToken;
        Assert.True(first.Writer.TryWrite(message));
        Assert.False(second.Writer.TryWrite(message));
        var wait = second.Writer.WaitToWriteAsync(token).AsTask();
        Assert.False(wait.IsCompleted);
        Assert.True(first.Reader.TryRead(out _));
        Assert.True(await wait.WaitAsync(TimeSpan.FromSeconds(5), token));
        Assert.Equal(0, budget.Status.ReservedBytes); // 等待可写不占用额度。

        Assert.True(first.Writer.TryWrite(message));
        wait = second.Writer.WaitToWriteAsync(token).AsTask();
        Assert.False(wait.IsCompleted);
        second.Writer.Complete();
        Assert.False(await wait.WaitAsync(TimeSpan.FromSeconds(5), token));
        Assert.Equal(message.BudgetBytes, budget.Status.ReservedBytes);
        Assert.True(first.Reader.TryRead(out _));
    }

    [Fact]
    public async Task ProductionReservesBeforeCallbackAndTransfersOnlyMessageCharge()
    {
        var message = Message();
        var budget = new InFlightBudget(message.BudgetBytes * 2 + 100);
        var channel = new BudgetedInputChannel(1, budget) { OutputReservationBytes = 100 };
        var writer = (InputMessageWriter)channel.Writer;
        Assert.True(budget.TryReserve(budget.Capacity));
        var called = false;
        var writing = writer.ProduceAsync(message.BudgetBytes * 2, _ =>
        {
            called = true;
            Assert.Equal(budget.Capacity, budget.Status.ReservedBytes);
            return ValueTask.FromResult<EdgeMessage?>(message);
        }, TestContext.Current.CancellationToken).AsTask();
        Assert.False(called);
        Assert.False(writing.IsCompleted);
        budget.Release(budget.Capacity);
        await writing.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(called);
        Assert.Equal(message.BudgetBytes + 100, budget.Status.ReservedBytes);
        Assert.True(channel.HoldingReader.TryRead(out var held));
        Assert.Equal(message.BudgetBytes + 100, budget.Status.ReservedBytes);
        channel.Release(held!);
        Assert.Equal(0, budget.Status.ReservedBytes);
    }

    [Fact]
    public async Task CancelledReservationDoesNotInvokeProducer()
    {
        var budget = new InFlightBudget(100);
        Assert.True(budget.TryReserve(100));
        var writer = (InputMessageWriter)new BudgetedInputChannel(1, budget).Writer;
        using var stop = new CancellationTokenSource();
        var called = false;
        var writing = writer.ProduceAsync(100, _ =>
        {
            called = true;
            return ValueTask.FromResult<EdgeMessage?>(null);
        }, stop.Token).AsTask();
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writing);
        Assert.False(called);
        Assert.Equal(100, budget.Status.ReservedBytes);
        budget.Release(100);
    }

    [Fact]
    public async Task CancellationDoesNotReleaseBudgetWhileProducerStillOwnsMemory()
    {
        var message = Message();
        var budget = new InFlightBudget(message.BudgetBytes);
        var channel = new BudgetedInputChannel(1, budget);
        var writer = (InputMessageWriter)channel.Writer;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = new CancellationTokenSource();
        var writing = writer.ProduceAsync(message.BudgetBytes, async _ =>
        {
            started.SetResult();
            await finish.Task; // 模拟迟迟不响应取消的读取；不能提前把仍存活的内存额度给别的输入。
            return message;
        }, stop.Token).AsTask();
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await stop.CancelAsync();
            Assert.False(writing.IsCompleted);
            Assert.Equal(message.BudgetBytes, budget.Status.ReservedBytes);
        }
        finally
        {
            finish.TrySetResult();
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writing);
        Assert.Equal(0, budget.Status.ReservedBytes);
        Assert.False(channel.Reader.TryRead(out _));
    }

    [Fact]
    public async Task EmptyFailedOversizedAndClosedProductionDoNotLeakReservations()
    {
        var message = Message();
        var budget = new InFlightBudget(message.BudgetBytes);
        var channel = new BudgetedInputChannel(1, budget);
        var writer = (InputMessageWriter)channel.Writer;
        var token = TestContext.Current.CancellationToken;
        await writer.ProduceAsync(message.BudgetBytes, _ => ValueTask.FromResult<EdgeMessage?>(null), token);
        Assert.Equal(0, budget.Status.ReservedBytes);
        await Assert.ThrowsAsync<IOException>(async () => await writer.ProduceAsync(message.BudgetBytes,
            _ => throw new IOException("test read failure"), token));
        Assert.Equal(0, budget.Status.ReservedBytes);
        await Assert.ThrowsAsync<InputMessageSizeException>(async () => await writer.ProduceAsync(1,
            _ => ValueTask.FromResult<EdgeMessage?>(message), token));
        Assert.Equal(0, budget.Status.ReservedBytes);
        await Assert.ThrowsAsync<InputMessageSizeException>(async () => await writer.ProduceAsync(budget.Capacity + 1,
            _ => throw new InvalidOperationException("Must not allocate before reservation"), token));
        channel.Writer.Complete();
        await Assert.ThrowsAsync<ChannelClosedException>(async () => await writer.ProduceAsync(message.BudgetBytes,
            _ => throw new InvalidOperationException("Must not read after completion"), token));
        Assert.Equal(0, budget.Status.ReservedBytes);
        Assert.False(channel.Reader.TryRead(out _));
    }

    [Fact]
    public async Task ChannelsShareBudgetAndHoldingReaderKeepsChargeUntilCommit()
    {
        var message = Message();
        var charge = message.BudgetBytes + 100;
        var budget = new InFlightBudget(charge);
        var first = new BudgetedInputChannel(10, budget) { OutputReservationBytes = 100 };
        var second = new BudgetedInputChannel(10, budget) { OutputReservationBytes = 100 };
        Assert.True(first.Writer.TryWrite(message));
        Assert.False(second.Writer.TryWrite(message));
        Assert.True(first.HoldingReader.TryRead(out var held));
        Assert.Equal(charge, budget.Status.ReservedBytes);
        var waiting = second.Writer.WriteAsync(message, TestContext.Current.CancellationToken).AsTask();
        Assert.False(waiting.IsCompleted);
        first.Release(held!);
        await waiting.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(second.Reader.TryRead(out _));
        Assert.Equal(0, budget.Status.ReservedBytes);
        Assert.Equal(charge, budget.Status.PeakReservedBytes);
    }

    [Fact]
    public async Task CancellationAndClosedChannelReleaseReservations()
    {
        var message = Message();
        var budget = new InFlightBudget(message.BudgetBytes * 2);
        var channel = new BudgetedInputChannel(1, budget);
        Assert.True(channel.Writer.TryWrite(message));
        using var cancel = new CancellationTokenSource();
        var waiting = channel.Writer.WriteAsync(message, cancel.Token).AsTask();
        Assert.Equal(message.BudgetBytes * 2, budget.Status.ReservedBytes);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Equal(message.BudgetBytes, budget.Status.ReservedBytes);
        channel.Writer.TryComplete();
        Assert.False(channel.Writer.TryWrite(message));
        Assert.True(channel.Reader.TryRead(out _));
        Assert.Equal(0, budget.Status.ReservedBytes);
    }

    [Fact]
    public async Task BudgetWaitCancellationDoesNotConsumeBytes()
    {
        var budget = new InFlightBudget(10);
        Assert.True(budget.TryReserve(10));
        using var cancel = new CancellationTokenSource();
        var wait = budget.ReserveAsync(1, cancel.Token).AsTask();
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
        budget.Release(10);
        Assert.Equal(0, budget.Status.ReservedBytes);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await budget.ReserveAsync(11, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClosingChannelWakesBudgetWaiterWithoutReleasingAnotherChannelsCharge(bool faulted)
    {
        var message = Message();
        var budget = new InFlightBudget(message.BudgetBytes);
        var holding = new BudgetedInputChannel(1, budget);
        var closing = new BudgetedInputChannel(1, budget);
        Assert.True(holding.Writer.TryWrite(message));
        var waiting = closing.Writer.WriteAsync(message, TestContext.Current.CancellationToken).AsTask();
        Assert.False(waiting.IsCompleted);

        var failure = faulted ? new IOException("test channel failure") : null;
        Assert.True(closing.Writer.TryComplete(failure));
        Assert.False(closing.Writer.TryComplete());
        var error = await Assert.ThrowsAsync<ChannelClosedException>(() =>
            waiting.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Same(failure, error.InnerException);
        await Assert.ThrowsAsync<ChannelClosedException>(async () =>
            await closing.Writer.WriteAsync(message, TestContext.Current.CancellationToken));
        Assert.Equal(message.BudgetBytes, budget.Status.ReservedBytes);
        Assert.True(holding.Reader.TryRead(out _));
        Assert.Equal(0, budget.Status.ReservedBytes);
    }

    private static EdgeMessage Message() => new(Guid.NewGuid(), "input", "device", DateTimeOffset.UnixEpoch,
        TimeSource.Gateway, DateTimeOffset.UnixEpoch, new Dictionary<string, FieldValue> { ["value"] = FieldValue.FromString("测试") });
}
