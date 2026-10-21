using TenonEdge.Runtime.Deliveries;

namespace TenonEdge.Runtime.Outputs;

internal sealed partial class OutputSenderState
{
    private QueuePause? _queuePause;
    private int _queueProbeReset;
    internal bool QueuePauseRequested => Volatile.Read(ref _queuePause) is not null;

    // 操作线程只提交信号。原循环（或监督者重建后的同一状态所有者）消费它，不跨线程清零毒消息探测字段。
    internal void RequestQueueProbeReset() => Volatile.Write(ref _queueProbeReset, 1);

    internal void ApplyQueueProbeReset()
    {
        if (Interlocked.Exchange(ref _queueProbeReset, 0) != 0)
        { RecheckHeadSeq = null; }
    }

    // 状态由监督者重建沿用。请求线程只发信号，不触碰 ACK/失败/退避等循环所有的数据。
    internal QueuePause PauseQueue()
    {
        var pause = new QueuePause(this);
        if (Interlocked.CompareExchange(ref _queuePause, pause, null) is not null)
        { throw new InvalidOperationException("目标已有队列操作。"); }
        return pause;
    }

    internal async Task WaitForQueuePauseAsync(DeliveryCache deliveries, TimeProvider time, CancellationToken token)
    {
        if (Volatile.Read(ref _queuePause) is not { } pause)
        { return; }
        // 在确认暂停前排空全部 ACK 回执：已成功发送的记录不能被人工清空或迁移为待发。
        await CommitAcknowledgementsAsync(deliveries, time, drain: true, token);
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
