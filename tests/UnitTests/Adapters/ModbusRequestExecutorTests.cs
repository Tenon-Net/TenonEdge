using TenonEdge.Adapters.Modbus;

namespace TenonEdge.UnitTests.Adapters;

public sealed class ModbusRequestExecutorTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task RequestsUseOneDedicatedThreadAndReadErrorsDoNotKillIt()
    {
        await using var executor = new ModbusRequestExecutor();
        var first = await executor.ExecuteAsync(() => (Environment.CurrentManagedThreadId, Thread.CurrentThread.IsThreadPoolThread), () => { }, TestContext.Current.CancellationToken);
        Assert.False(first.IsThreadPoolThread);
        await Assert.ThrowsAsync<IOException>(() => executor.ExecuteAsync<int>(() => throw new IOException("injected"), () => { }, TestContext.Current.CancellationToken));
        var second = await executor.ExecuteAsync(() => Environment.CurrentManagedThreadId, () => { }, TestContext.Current.CancellationToken);
        Assert.Equal(first.CurrentManagedThreadId, second);
    }

    [Fact]
    public async Task QueuedCancellationNeverAbortsActiveRead()
    {
        await using var executor = new ModbusRequestExecutor();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        var active = executor.ExecuteAsync(() => { entered.Set(); Assert.True(release.Wait(Limit, TestContext.Current.CancellationToken)); return 7; }, () => { }, TestContext.Current.CancellationToken);
        Assert.True(entered.Wait(Limit, TestContext.Current.CancellationToken));
        var aborted = 0;
        var ran = 0;
        var queued = executor.ExecuteAsync(() => { Interlocked.Increment(ref ran); return 8; }, () => Interlocked.Increment(ref aborted), cancellation.Token);
        cancellation.Cancel();
        Assert.Equal(0, Volatile.Read(ref aborted));
        release.Set();
        Assert.Equal(7, await active.WaitAsync(Limit, TestContext.Current.CancellationToken));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued.WaitAsync(Limit, TestContext.Current.CancellationToken));
        Assert.Equal(0, ran);
        Assert.Equal(0, aborted);
    }

    [Fact]
    public async Task ActiveCancellationDoesNotReleaseSlotUntilReadActuallyReturns()
    {
        await using var executor = new ModbusRequestExecutor();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var aborted = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        var active = executor.ExecuteAsync(() => { entered.Set(); Assert.True(release.Wait(Limit, TestContext.Current.CancellationToken)); return 7; }, aborted.Set, cancellation.Token);
        Assert.True(entered.Wait(Limit, TestContext.Current.CancellationToken));
        cancellation.Cancel();
        Assert.True(aborted.Wait(Limit, TestContext.Current.CancellationToken));
        var nextRan = 0;
        var next = executor.ExecuteAsync(() => Interlocked.Increment(ref nextRan), () => { }, TestContext.Current.CancellationToken);
        Assert.False(active.IsCompleted);
        Assert.Equal(0, Volatile.Read(ref nextRan));
        release.Set();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => active.WaitAsync(Limit, TestContext.Current.CancellationToken));
        Assert.Equal(1, await next.WaitAsync(Limit, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DisposeAbortsCurrentReadCancelsQueuedReadAndRejectsNewWork()
    {
        await using var executor = new ModbusRequestExecutor();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var active = executor.ExecuteAsync(() => { entered.Set(); Assert.True(release.Wait(Limit, TestContext.Current.CancellationToken)); return 7; }, release.Set, TestContext.Current.CancellationToken);
        Assert.True(entered.Wait(Limit, TestContext.Current.CancellationToken));
        var ran = 0;
        var queued = executor.ExecuteAsync(() => Interlocked.Increment(ref ran), () => { }, TestContext.Current.CancellationToken);
        await executor.DisposeAsync().AsTask().WaitAsync(Limit, TestContext.Current.CancellationToken);
        await executor.DisposeAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => active);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        Assert.Equal(0, ran);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => executor.ExecuteAsync(() => 1, () => { }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DisposalCannotFinishWhileCancellationIgnoringReadStillOwnsThread()
    {
        await using var executor = new ModbusRequestExecutor();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var aborted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = executor.ExecuteAsync(() =>
        {
            entered.Set();
            Assert.True(release.Wait(Limit, TestContext.Current.CancellationToken));
            return 1;
        }, () => aborted.SetResult(), TestContext.Current.CancellationToken);
        Assert.True(entered.Wait(Limit, TestContext.Current.CancellationToken));
        var disposing = executor.DisposeAsync().AsTask();
        await aborted.Task.WaitAsync(Limit, TestContext.Current.CancellationToken);
        Assert.False(disposing.IsCompleted);
        Assert.False(active.IsCompleted);
        release.Set();
        await disposing.WaitAsync(Limit, TestContext.Current.CancellationToken);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => active);
    }

    [Fact]
    public async Task WaitingForFullQueueCanBeCancelledWithoutRunningOrAborting()
    {
        await using var executor = new ModbusRequestExecutor();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        var first = executor.ExecuteAsync(() => { entered.Set(); Assert.True(release.Wait(Limit, TestContext.Current.CancellationToken)); return 1; }, release.Set, TestContext.Current.CancellationToken);
        Assert.True(entered.Wait(Limit, TestContext.Current.CancellationToken));
        var second = executor.ExecuteAsync(() => 2, () => { }, TestContext.Current.CancellationToken);
        var ran = 0;
        var third = executor.ExecuteAsync(() => Interlocked.Increment(ref ran), () => Interlocked.Increment(ref ran), cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => third.WaitAsync(Limit, TestContext.Current.CancellationToken));
        release.Set();
        Assert.Equal(1, await first.WaitAsync(Limit, TestContext.Current.CancellationToken));
        Assert.Equal(2, await second.WaitAsync(Limit, TestContext.Current.CancellationToken));
        Assert.Equal(0, ran);
    }
}
