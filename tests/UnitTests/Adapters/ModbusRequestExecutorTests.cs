using TenonEdge.Adapters.Modbus;

namespace TenonEdge.UnitTests.Adapters;

public sealed class ModbusRequestExecutorTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task LongLivedWorkerDoesNotInheritCreatingRequestsExecutionContext()
    {
        var testToken = TestContext.Current.CancellationToken;
        var requestContext = new AsyncLocal<object?>();
        var requestObject = new object();
        requestContext.Value = requestObject;
        try
        {
            await using var executor = new ModbusRequestExecutor();
            var inherited = await executor.ExecuteAsync(() => requestContext.Value, () => { }, testToken);
            Assert.Null(inherited);
            Assert.Same(requestObject, requestContext.Value);
        }
        finally { requestContext.Value = null; }
    }

    [Fact]
    public async Task RequestsUseOneDedicatedThreadAndReadErrorsDoNotKillIt()
    {
        var testToken = TestContext.Current.CancellationToken;
        await using var executor = new ModbusRequestExecutor();
        var first = await executor.ExecuteAsync(() => (Environment.CurrentManagedThreadId, Thread.CurrentThread.IsThreadPoolThread), () => { }, testToken);
        Assert.False(first.IsThreadPoolThread);
        await Assert.ThrowsAsync<IOException>(() => executor.ExecuteAsync<int>(() => throw new IOException("injected"), () => { }, testToken));
        var second = await executor.ExecuteAsync(() => Environment.CurrentManagedThreadId, () => { }, testToken);
        Assert.Equal(first.CurrentManagedThreadId, second);
    }

    [Fact]
    public async Task QueuedCancellationNeverAbortsActiveRead()
    {
        var testToken = TestContext.Current.CancellationToken;
        await using var executor = new ModbusRequestExecutor();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        var active = executor.ExecuteAsync(() => { entered.Set(); Assert.True(release.Wait(Limit, testToken)); return 7; }, () => { }, testToken);
        Assert.True(entered.Wait(Limit, testToken));
        var aborted = 0;
        var ran = 0;
        var queued = executor.ExecuteAsync(() => { Interlocked.Increment(ref ran); return 8; }, () => Interlocked.Increment(ref aborted), cancellation.Token);
        cancellation.Cancel();
        Assert.Equal(0, Volatile.Read(ref aborted));
        release.Set();
        Assert.Equal(7, await active.WaitAsync(Limit, testToken));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued.WaitAsync(Limit, testToken));
        Assert.Equal(0, ran);
        Assert.Equal(0, aborted);
    }

    [Fact]
    public async Task ActiveCancellationDoesNotReleaseSlotUntilReadActuallyReturns()
    {
        var testToken = TestContext.Current.CancellationToken;
        await using var executor = new ModbusRequestExecutor();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var aborted = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        var active = executor.ExecuteAsync(() => { entered.Set(); Assert.True(release.Wait(Limit, testToken)); return 7; }, aborted.Set, cancellation.Token);
        Assert.True(entered.Wait(Limit, testToken));
        cancellation.Cancel();
        Assert.True(aborted.Wait(Limit, testToken));
        var nextRan = 0;
        var next = executor.ExecuteAsync(() => Interlocked.Increment(ref nextRan), () => { }, testToken);
        Assert.False(active.IsCompleted);
        Assert.Equal(0, Volatile.Read(ref nextRan));
        release.Set();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => active.WaitAsync(Limit, testToken));
        Assert.Equal(1, await next.WaitAsync(Limit, testToken));
    }

    [Fact]
    public async Task DisposeAbortsCurrentReadCancelsQueuedReadAndRejectsNewWork()
    {
        var testToken = TestContext.Current.CancellationToken;
        await using var executor = new ModbusRequestExecutor();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var active = executor.ExecuteAsync(() => { entered.Set(); Assert.True(release.Wait(Limit, testToken)); return 7; }, release.Set, testToken);
        Assert.True(entered.Wait(Limit, testToken));
        var ran = 0;
        var queued = executor.ExecuteAsync(() => Interlocked.Increment(ref ran), () => { }, testToken);
        await executor.DisposeAsync().AsTask().WaitAsync(Limit, testToken);
        await executor.DisposeAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => active);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        Assert.Equal(0, ran);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => executor.ExecuteAsync(() => 1, () => { }, testToken));
    }

    [Fact]
    public async Task DisposalCannotFinishWhileCancellationIgnoringReadStillOwnsThread()
    {
        var testToken = TestContext.Current.CancellationToken;
        await using var executor = new ModbusRequestExecutor();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var aborted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = executor.ExecuteAsync(() =>
        {
            entered.Set();
            Assert.True(release.Wait(Limit, testToken));
            return 1;
        }, () => aborted.SetResult(), testToken);
        Assert.True(entered.Wait(Limit, testToken));
        var disposing = executor.DisposeAsync().AsTask();
        await aborted.Task.WaitAsync(Limit, testToken);
        Assert.False(disposing.IsCompleted);
        Assert.False(active.IsCompleted);
        release.Set();
        await disposing.WaitAsync(Limit, testToken);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => active);
    }

    [Fact]
    public async Task WaitingForFullQueueCanBeCancelledWithoutRunningOrAborting()
    {
        var testToken = TestContext.Current.CancellationToken;
        await using var executor = new ModbusRequestExecutor();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        var first = executor.ExecuteAsync(() => { entered.Set(); Assert.True(release.Wait(Limit, testToken)); return 1; }, release.Set, testToken);
        Assert.True(entered.Wait(Limit, testToken));
        var second = executor.ExecuteAsync(() => 2, () => { }, testToken);
        var ran = 0;
        var third = executor.ExecuteAsync(() => Interlocked.Increment(ref ran), () => Interlocked.Increment(ref ran), cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => third.WaitAsync(Limit, testToken));
        release.Set();
        Assert.Equal(1, await first.WaitAsync(Limit, testToken));
        Assert.Equal(2, await second.WaitAsync(Limit, testToken));
        Assert.Equal(0, ran);
    }
}
