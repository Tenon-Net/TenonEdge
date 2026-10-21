using TenonEdge.Runtime.Deliveries;

namespace TenonEdge.Runtime.Outputs;

internal sealed partial class OutputSenderState
{
    // 只保留编号和唯一写者的提交回执，不预取正文、不提前释放持久队列配额。
    // 上限同时约束存储停滞时的内存和重放窗口；通用 HTTP 仍最多一个网络请求在途。
    internal const int MaximumPendingAcknowledgements = 128;
    private readonly Queue<Acknowledgement> _acknowledgements = [];
    private readonly HashSet<long> _acknowledged = [];
    private long _acknowledgedThrough;
    private bool _acknowledgementCommitFailed;

    private sealed class Acknowledgement(long sequence)
    {
        internal long Sequence { get; } = sequence;
        internal Task<DeliveryWriteResult>? Receipt { get; set; }
    }

    internal int PendingAcknowledgementCount => _acknowledgements.Count;

    internal void Acknowledge(long sequence, long headSequence)
    {
        if (!_acknowledged.Add(sequence))
        { throw new InvalidOperationException("同一进程不能重复发送已确认的记录。"); }
        _acknowledgements.Enqueue(new(sequence));
        if (sequence == headSequence)
        { _acknowledgedThrough = sequence; }
    }

    internal StoredDelivery? ReadUnacknowledgedHead(DeliveryStore.PendingReader pendingReader)
    {
        while (true)
        {
            var head = pendingReader.Read(_acknowledgedThrough);
            if (head is null)
            { return null; }
            if (!_acknowledged.Contains(head.Seq))
            { return head; }
            // 毒消息的成功探测可能先确认队头之后的记录；不能用最大 ACK 编号越过未确认队头。
            _acknowledgedThrough = head.Seq;
        }
    }

    internal async Task CommitAcknowledgementsAsync(DeliveryCache deliveries, TimeProvider time, bool drain, CancellationToken token)
    {
        drain |= _acknowledgementCommitFailed;
        foreach (var acknowledgement in _acknowledgements)
        {
            if (_acknowledgementCommitFailed && acknowledgement.Receipt is { IsCompletedSuccessfully: true } previous &&
                previous.Result.Status == DeliveryWriteStatus.StorageFailed)
            { acknowledgement.Receipt = null; }
            // 回执跨监督者重建保留。取消运行循环只取消等待，不撤回已交给唯一写者的请求。
            acknowledgement.Receipt ??= deliveries.DeleteAsync([acknowledgement.Sequence], CancellationToken.None);
        }
        while (_acknowledgements.TryPeek(out var acknowledgement) &&
            (drain || _acknowledgements.Count >= MaximumPendingAcknowledgements || acknowledgement.Receipt!.IsCompleted))
        {
            var result = await acknowledgement.Receipt!.WaitAsync(token);
            if (result.Status == DeliveryWriteStatus.Stopped)
            { await Task.Delay(Timeout.InfiniteTimeSpan, time, token); }
            if (!result.Committed)
            {
                acknowledgement.Receipt = null;
                _acknowledgementCommitFailed = true;
                throw new InvalidOperationException("已确认记录删除未提交，重试删除而不重发。");
            }
            _acknowledgements.Dequeue();
            _acknowledged.Remove(acknowledgement.Sequence);
        }
        if (_acknowledgements.Count == 0)
        {
            _acknowledgedThrough = 0;
            _acknowledgementCommitFailed = false;
        }
    }
}
