using TenonEdge.Runtime.Deliveries;
using TenonEdge.Runtime.Outputs;

namespace TenonEdge.Runtime.Lifecycle;

public sealed partial class RuntimeHost
{
    /// <summary>宿主已授权并确认清空目标全部待发。与生命周期操作互斥，原发送者提交在途结果后才删除；
    /// 超时不删除、不终止旧发送者。不改变手动/自动暂停和重试状态。请求取消仅结束等待。</summary>
    public Task<DeliveryWriteResult> ClearPendingAsync(long connectionId, long revision, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (connectionId is < 1 or > 9007199254740991L || revision < 1)
        { throw new ArgumentException("清空目标或有效版本无效。"); }
        var operation = EnterOperation();
        return ClearPendingOwnedAsync(connectionId, revision, operation).WaitAsync(token);
    }

    private async Task<DeliveryWriteResult> ClearPendingOwnedAsync(long connectionId, long revision, TaskCompletionSource operation)
    {
        try
        {
            OutputSenderState sender;
            lock (_lock)
            {
                if (_activeRevision != revision || !_activeOutputs.Any(output => output.Target.ConnectionId == connectionId) ||
                    !_senderStates.TryGetValue(connectionId, out sender!) ||
                    !_supervisor.Snapshot().Any(unit => unit.Id == DeliveryCache.WriterId && unit.State == RuntimeUnitState.Running))
                { return DeliveryWriteResult.Rejected("delivery.clearPendingUnavailable"); }
            }
            using var pause = sender.PauseQueue();
            try
            { await pause.Reached.Task.WaitAsync(_options.UnitStopTimeout, _time, CancellationToken.None); }
            catch (TimeoutException)
            { return DeliveryWriteResult.Rejected("delivery.clearPendingNotQuiescent"); }
            // 唯一写者在一个事务中删除执行边界时仍待发的全部记录及累计人工丢弃数。
            // 入库继续；该 DELETE 之后入库的记录保留。不遍历客户端选择，不调用协议 ACK 删除。
            return await _deliveries.ClearPendingAsync(connectionId);
        }
        finally { CompleteOperation(operation); }
    }
}
