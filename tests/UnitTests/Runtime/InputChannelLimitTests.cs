using System.Threading.Channels;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Runtime.Diagnostics;

namespace TenonEdge.UnitTests.Runtime;

public sealed class InputChannelLimitTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static EdgeMessage Message()
    {
        var now = DateTimeOffset.UtcNow;
        return new(Guid.NewGuid(), "source", "device", now, TimeSource.Gateway, now,
            new Dictionary<string, FieldValue> { ["value"] = FieldValue.FromInt64(1) });
    }

    [Fact]
    public async Task RaiseWakesExistingChannelWithoutReleasingOldByteOwnership()
    {
        var limit = new InputChannelLimit(1);
        var bytes = new InFlightBudget(100000);
        var channel = new BudgetedInputChannel(1, bytes, limits: limit);
        var first = Message();
        var second = Message();
        Assert.True(channel.Writer.TryWrite(first));
        var pending = channel.Writer.WriteAsync(second, Token).AsTask();
        Assert.False(pending.IsCompleted);
        Assert.Equal(first.BudgetBytes + second.BudgetBytes, bytes.Status.ReservedBytes);
        limit.Configure(2);
        await pending.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.Equal(2, channel.Reader.Count);
        Assert.True(channel.HoldingReader.TryRead(out var held));
        Assert.Same(first, held);
        Assert.Equal(first.BudgetBytes + second.BudgetBytes, bytes.Status.ReservedBytes);
        channel.Release(first);
        Assert.True(channel.Reader.TryRead(out var last));
        Assert.Same(second, last);
        Assert.Equal(0, bytes.Status.ReservedBytes);
    }

    [Fact]
    public async Task LoweringPreservesFifoAndOtherChannelsUseSameLimitIndependently()
    {
        var limit = new InputChannelLimit(3);
        var bytes = new InFlightBudget(100000);
        var first = new BudgetedInputChannel(3, bytes, limits: limit);
        var second = new BudgetedInputChannel(3, bytes, limits: limit);
        var messages = Enumerable.Range(0, 3).Select(_ => Message()).ToArray();
        foreach (var message in messages)
        { Assert.True(first.Writer.TryWrite(message)); }
        limit.Configure(1);
        Assert.Equal(3, first.Reader.Count);
        Assert.False(first.Writer.TryWrite(Message()));
        Assert.True(second.Writer.TryWrite(Message()));
        Assert.False(second.Writer.TryWrite(Message()));
        var available = first.Writer.WaitToWriteAsync(Token).AsTask();
        Assert.False(available.IsCompleted);
        foreach (var message in messages)
        {
            Assert.True(first.Reader.TryRead(out var item));
            Assert.Same(message, item);
            if (first.Reader.Count > 0)
            { Assert.False(available.IsCompleted); }
        }
        Assert.True(await available.WaitAsync(TimeSpan.FromSeconds(5), Token));
        Assert.True(first.Writer.TryWrite(Message()));
        Assert.True(first.Reader.TryRead(out _));
        Assert.True(second.Reader.TryRead(out _));
        Assert.Equal(0, bytes.Status.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResizedFullChannelClosesWaitersWithOriginalCompletionSemantics(bool error)
    {
        var limit = new InputChannelLimit(2);
        var bytes = new InFlightBudget(100000);
        var channel = new BudgetedInputChannel(2, bytes, limits: limit);
        Assert.True(channel.Writer.TryWrite(Message()));
        limit.Configure(1);
        var waiting = channel.Writer.WaitToWriteAsync(Token).AsTask();
        Assert.False(waiting.IsCompleted);
        Assert.True(channel.Writer.TryComplete(error ? new InvalidOperationException("fixture") : null));
        if (error)
        { await Assert.ThrowsAsync<InvalidOperationException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(5), Token)); }
        else
        { Assert.False(await waiting.WaitAsync(TimeSpan.FromSeconds(5), Token)); }
        Assert.True(channel.Reader.TryRead(out _));
        Assert.Equal(0, bytes.Status.ReservedBytes);
    }

    [Fact]
    public async Task RepeatedCancelledPublicationNeverAccumulatesMessagesOrReservedBytes()
    {
        var limit = new InputChannelLimit(1);
        var bytes = new InFlightBudget(100000);
        var channel = new BudgetedInputChannel(1, bytes, limits: limit);
        var first = Message();
        Assert.True(channel.Writer.TryWrite(first));
        for (var i = 0; i < 500; i++)
        {
            using var cancel = new CancellationTokenSource();
            var waiting = channel.Writer.WriteAsync(Message(), cancel.Token).AsTask();
            await cancel.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
            Assert.Equal(1, channel.Reader.Count);
            Assert.Equal(first.BudgetBytes, bytes.Status.ReservedBytes);
        }
        limit.Configure(2);
        Assert.True(channel.Writer.TryWrite(Message()));
        Assert.True(channel.Reader.TryRead(out _));
        Assert.True(channel.Reader.TryRead(out _));
        Assert.Equal(0, bytes.Status.ReservedBytes);
    }

    [Fact]
    public void InvalidUpdatesKeepOldLimitAndFullQosZeroPathDoesNotProduce()
    {
        var limit = new InputChannelLimit(2);
        var channel = new BudgetedInputChannel(2, new InFlightBudget(100000), limits: limit);
        Assert.True(channel.Writer.TryWrite(Message()));
        limit.Configure(1);
        var invoked = false;
        Assert.False(((InputMessageWriter)channel.Writer).TryProduce(1000, 1000, () => { invoked = true; return Message(); }, Token));
        Assert.False(invoked);
        Assert.Throws<ArgumentOutOfRangeException>(() => limit.Configure(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => limit.Configure(10001));
        Assert.Equal(1, limit.Capacity);
        Assert.True(channel.Reader.TryRead(out _));
    }
}
