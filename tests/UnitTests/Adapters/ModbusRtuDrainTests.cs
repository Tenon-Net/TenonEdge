using Microsoft.Extensions.Time.Testing;
using TenonEdge.Adapters.Modbus;

namespace TenonEdge.UnitTests.Adapters;

public sealed class ModbusRtuDrainTests
{
    [Fact]
    public async Task ContinuousBytesDoNotResetDeadlineOrGrowBuffer()
    {
        var time = new FakeTimeProvider();
        using var stream = new ContinuousStream(time);
        var started = time.GetTimestamp();
        var result = await ModbusRtuDrain.DrainAsync(stream, TimeSpan.FromMilliseconds(500), time, TestContext.Current.CancellationToken);
        Assert.False(result.CanReuseConnection);
        Assert.Equal(5 * 256, result.DiscardedBytes);
        Assert.Equal(TimeSpan.FromMilliseconds(500), time.GetElapsedTime(started));
        Assert.Equal(256, stream.MaximumReadSize);
    }

    [Fact]
    public async Task EofRequiresReconnectAndExternalCancellationIsNotSilence()
    {
        using var stream = new MemoryStream();
        Assert.False((await ModbusRtuDrain.DrainAsync(stream, TimeSpan.FromSeconds(1), TimeProvider.System, TestContext.Current.CancellationToken)).CanReuseConnection);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ModbusRtuDrain.DrainAsync(stream, TimeSpan.FromSeconds(1), TimeProvider.System, cancelled.Token));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ModbusRtuDrain.DrainAsync(stream, TimeSpan.Zero, TimeProvider.System, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeadlineDoesNotLeaveAnUnfinishedReadBehind()
    {
        var time = new FakeTimeProvider();
        using var stream = new DeferredStream();
        var drain = ModbusRtuDrain.DrainAsync(stream, TimeSpan.FromMilliseconds(500), time, TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromMilliseconds(500));
        Assert.False(drain.IsCompleted);
        stream.Response.SetResult(1);
        var result = await drain.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.False(result.CanReuseConnection);
        Assert.Equal(1, result.DiscardedBytes);
    }

    [Fact]
    public async Task CancellationDuringReadPropagatesInsteadOfAuthorizingReuse()
    {
        using var stream = new DeferredStream();
        using var cancellation = new CancellationTokenSource();
        var drain = ModbusRtuDrain.DrainAsync(stream, TimeSpan.FromSeconds(10), TimeProvider.System, cancellation.Token);
        cancellation.Cancel();
        stream.Response.SetResult(0);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => drain.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }

    private sealed class DeferredStream : MemoryStream
    {
        public TaskCompletionSource<int> Response { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => new(Response.Task);
    }

    private sealed class ContinuousStream(FakeTimeProvider time) : Stream
    {
        public int MaximumReadSize { get; private set; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            MaximumReadSize = Math.Max(MaximumReadSize, buffer.Length);
            time.Advance(TimeSpan.FromMilliseconds(100));
            buffer.Span.Fill(0xff);
            return ValueTask.FromResult(buffer.Length);
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
