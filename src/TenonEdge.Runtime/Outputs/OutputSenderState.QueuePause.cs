namespace TenonEdge.Runtime.Outputs;

internal sealed partial class OutputSenderState
{
    private QueuePause? _queuePause;
    internal bool QueuePauseRequested => Volatile.Read(ref _queuePause) is not null;

    // 状态由监督者重建沿用。请求线程只发信号，不触碰 ACK/失败/退避等循环所有的数据。
    internal QueuePause PauseQueue()
    {
        var pause = new QueuePause(this);
        if (Interlocked.CompareExchange(ref _queuePause, pause, null) is not null)
        { throw new InvalidOperationException("目标已有队列操作。"); }
        return pause;
    }

    internal async Task WaitForQueuePauseAsync(CancellationToken token)
    {
        if (Volatile.Read(ref _queuePause) is not { } pause)
        { return; }
        pause.Reached.TrySetResult();
        await pause.Released.Task.WaitAsync(token);
    }

    internal sealed class QueuePause(OutputSenderState owner) : IDisposable
    {
        internal TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Dispose()
        {
            Interlocked.CompareExchange(ref owner._queuePause, null, this);
            Released.TrySetResult();
        }
    }
}
