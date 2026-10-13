using TenonEdge.Abstractions.Contracts;
using TenonEdge.Runtime.Deliveries;

namespace TenonEdge.Runtime.Lifecycle;

public sealed partial class RuntimeHost
{
    /// <summary>协调者已在配置事务中登记意图后调用：在线须持有本次成功静默及该源发送屏障；
    /// 启动恢复须已启动原写者、尚未激活任何发送者。不能在 COMMIT 前调用，不接受客户端自报意图。
    /// 隔离失败不释放屏障、不激活新版本；调用方取消只结束等待。</summary>
    public Task<DeliveryWriteResult> HoldMigrationAsync(DeliveryMigrationHold intent, RuntimeQuiescenceResult? quiescence, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(intent);
        token.ThrowIfCancellationRequested();
        var operation = EnterOperation();
        return HoldMigrationOwnedAsync(intent, quiescence, operation).WaitAsync(token);
    }

    private async Task<DeliveryWriteResult> HoldMigrationOwnedAsync(DeliveryMigrationHold intent,
        RuntimeQuiescenceResult? quiescence, TaskCompletionSource operation)
    {
        try
        {
            lock (_lock)
            {
                if (!_coreStarted || !_supervisor.Snapshot().Any(unit => unit.Id == DeliveryCache.WriterId && unit.State == RuntimeUnitState.Running) ||
                    (_activeRevision is not null && (!ReferenceEquals(_currentQuiescence, quiescence) ||
                        quiescence?.Revision != _activeRevision || !_applicationSenderPauses.ContainsKey(intent.ConnectionId))) ||
                    (_activeRevision is null && quiescence is not null))
                { return DeliveryWriteResult.Rejected(DeliveryMigrationErrors.NotQuiescent); }
            }
            return await _deliveries.HoldMigrationAsync(intent);
        }
        finally { CompleteOperation(operation); }
    }
}
