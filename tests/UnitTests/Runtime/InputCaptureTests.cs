using System.Threading.Channels;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Runtime.Diagnostics;

namespace TenonEdge.UnitTests.Runtime;

public sealed class InputCaptureTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task AllPublicationPathsCaptureExactlyOnceAndKeepBudgetUntilRelease(int mode)
    {
        var message = Message();
        var captured = new List<EdgeMessage>();
        var budget = new InFlightBudget(10000);
        var channel = new BudgetedInputChannel(1, budget, captured.Add) { UsesDurableHandoff = mode == 4 };
        var writer = (InputMessageWriter)channel.Writer;
        Task<InputHandoffStatus>? receipt = null;
        switch (mode)
        {
            case 0:
                Assert.True(writer.TryWrite(message));
                break;
            case 1:
                await writer.WriteAsync(message, TestContext.Current.CancellationToken);
                break;
            case 2:
                Assert.True(writer.TryProduce(10000, 10000, () => message, TestContext.Current.CancellationToken));
                break;
            case 3:
                await writer.ProduceAsync(10000, 10000, _ => ValueTask.FromResult<EdgeMessage?>(message), TestContext.Current.CancellationToken);
                break;
            case 4:
                receipt = writer.ProduceConfirmedAsync(10000, 10000, _ => ValueTask.FromResult<EdgeMessage?>(message), TestContext.Current.CancellationToken).AsTask();
                break;
        }
        Assert.Same(message, Assert.Single(captured));
        Assert.True(channel.HoldingReader.TryRead(out var held));
        Assert.Equal(channel.Charge(message), budget.Status.ReservedBytes);
        if (receipt is not null)
        { Assert.False(receipt.IsCompleted); }
        channel.Release(held!, InputHandoffStatus.Committed);
        if (receipt is not null)
        { Assert.Equal(InputHandoffStatus.Committed, await receipt); }
        Assert.Single(captured);
        Assert.Equal(0, budget.Status.ReservedBytes);
    }

    [Fact]
    public async Task FullCancelledClosedAndNullProductionsDoNotCapture()
    {
        var captured = new List<EdgeMessage>();
        var channel = new BudgetedInputChannel(1, new InFlightBudget(20000), captured.Add);
        var writer = (InputMessageWriter)channel.Writer;
        await writer.ProduceAsync(10000, _ => ValueTask.FromResult<EdgeMessage?>(null), TestContext.Current.CancellationToken);
        Assert.Empty(captured);
        Assert.True(writer.TryWrite(Message()));
        Assert.False(writer.TryWrite(Message()));
        using var cancellation = new CancellationTokenSource();
        var pending = writer.WriteAsync(Message(), cancellation.Token).AsTask();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Single(captured);
        writer.TryComplete();
        Assert.False(writer.TryWrite(Message()));
        await Assert.ThrowsAsync<ChannelClosedException>(async () => await writer.WriteAsync(Message(), TestContext.Current.CancellationToken));
        Assert.Single(captured);
        Assert.True(channel.Reader.TryRead(out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DiagnosticFailureDoesNotRejectAcceptedMessageOrItsDurableHandoff(bool allocationFailure)
    {
        var budget = new InFlightBudget(10000);
        var channel = new BudgetedInputChannel(1, budget, _ =>
        {
            if (allocationFailure)
            { throw new DiagnosticAllocationFailure(); }
            throw new InvalidOperationException("fixture");
        })
        { UsesDurableHandoff = true };
        var receipt = ((InputMessageWriter)channel.Writer).ProduceConfirmedAsync(10000, 10000,
            _ => ValueTask.FromResult<EdgeMessage?>(Message()), TestContext.Current.CancellationToken).AsTask();
        Assert.Equal(1, channel.CaptureFailures);
        Assert.False(receipt.IsCompleted);
        Assert.True(channel.HoldingReader.TryRead(out var message));
        channel.Release(message!, InputHandoffStatus.Committed);
        Assert.Equal(InputHandoffStatus.Committed, await receipt);
        Assert.Equal(0, budget.Status.ReservedBytes);
    }

    [Fact]
    public async Task ConsumerCannotReleaseBudgetWhileCaptureStillOwnsMessage()
    {
        var budget = new InFlightBudget(10000);
        using var entered = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        var channel = new BudgetedInputChannel(1, budget, _ =>
        {
            entered.Set();
            Assert.True(resume.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        });
        var message = Message();
        var producer = Task.Run(() => channel.Writer.TryWrite(message), TestContext.Current.CancellationToken);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.True(channel.HoldingReader.TryRead(out var held));
        var releasing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = Task.Run(() =>
        {
            releasing.SetResult();
            channel.Release(held!);
        }, TestContext.Current.CancellationToken);
        try
        {
            await releasing.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<TimeoutException>(() => release.WaitAsync(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken));
            Assert.Equal(message.BudgetBytes, budget.Status.ReservedBytes);
        }
        finally { resume.Set(); }
        Assert.True(await producer);
        await release;
        Assert.Equal(0, channel.CaptureFailures);
        Assert.Equal(0, budget.Status.ReservedBytes);
    }

    private sealed class DiagnosticAllocationFailure : OutOfMemoryException { }

    private static EdgeMessage Message()
    {
        var time = DateTimeOffset.UtcNow;
        return new(Guid.NewGuid(), "input", "device", time, TimeSource.Gateway, time, []);
    }
}
