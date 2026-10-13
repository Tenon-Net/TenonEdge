using TenonEdge.Host.Features.Audit;
using TenonEdge.Runtime.Deliveries;
using TenonEdge.Runtime.Lifecycle;

namespace TenonEdge.Host.Features.Runtime;

internal sealed class DeliveryRetryUnavailableException : InvalidOperationException;
internal sealed class DeliveryCleanupUnavailableException : InvalidOperationException;
internal sealed class DeliveryClearPendingUnavailableException : InvalidOperationException;

internal sealed partial class RuntimeCoordinator
{
    private TaskCompletionSource? _deliveryWrite;

    /// <summary>一次最多1000条、同时最多一个队列管理写入；与应用/回滚/运行重试及停机互斥。
    /// 只保留复制后的非秘密参数，断开HTTP请求仅取消等待，宿主仍拥有原写者提交及审计的生命周期。</summary>
    public Task<DeliveryWriteResult> RetryFailedDeliveriesAsync(long revision, IReadOnlyList<DeliveryRetryItem> items,
        string account, string? sourceIp, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(account);
        ArgumentNullException.ThrowIfNull(items);
        if (revision < 1 || items.Count is < 1 or > 1000 || items.Any(item => item is null || item.Sequence < 1 || item.DeliveryId == Guid.Empty) ||
            items.Select(item => item.Sequence).Distinct().Count() != items.Count || items.Select(item => item.DeliveryId).Distinct().Count() != items.Count)
        { throw new ArgumentException("失败记录重试选择无效。"); }
        var selected = items.ToArray();
        TaskCompletionSource operation;
        IReadOnlyDictionary<long, string> targets;
        lock (_lock)
        {
            if (_stopping is not null || _application is not null || _deliveryWrite is not null ||
                _state != RuntimeCoordinatorState.Active || _activeRevision != revision)
            { throw new DeliveryRetryUnavailableException(); }
            targets = runtime.CaptureOutputFingerprints(revision) ?? throw new DeliveryRetryUnavailableException();
            if (targets.Count == 0)
            { throw new DeliveryRetryUnavailableException(); }
            operation = NewSignal();
            _deliveryWrite = operation;
        }
        return OwnDeliveryWrite(() => runtime.Deliveries.RetryFailedAsync(selected, targets, CancellationToken.None),
            _ => new("delivery.retryFailed", AuditResults.Success, account, sourceIp, Target(revision),
                $"已将{selected.Length}条失败记录移至队尾；保留投递编号，可能重复，不代表已发送。"),
            "delivery.retryOutcomeUnknown", operation, token);
    }

    /// <summary>清理可处理无有效输出/运行失败时遗留的失败和隔离数据，但写者须可用；不把“没有配置”当作空队列。</summary>
    public Task<DeliveryWriteResult> CleanupDeliveriesAsync(DeliveryCleanupSelection selection,
        string account, string? sourceIp, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentException.ThrowIfNullOrWhiteSpace(account);
        TaskCompletionSource operation;
        lock (_lock)
        {
            if (_stopping is not null || _application is not null || _deliveryWrite is not null ||
                _state is not (RuntimeCoordinatorState.Active or RuntimeCoordinatorState.Idle or RuntimeCoordinatorState.RunFailed) ||
                !runtime.GetStatus().Units.Any(unit => unit.Id == DeliveryCache.WriterId && unit.State == RuntimeUnitState.Running))
            { throw new DeliveryCleanupUnavailableException(); }
            operation = NewSignal();
            _deliveryWrite = operation;
        }
        var area = selection.State == DeliveryState.Failed ? "failed" : "quarantined";
        return OwnDeliveryWrite(() => runtime.Deliveries.CleanupAsync(selection, CancellationToken.None),
            _ => new("delivery.cleanup", AuditResults.Success, account, sourceIp, area,
                $"已按确认永久清理{selection.Items.Count}条记录，涉及{selection.Items.Select(item => item.ConnectionId).Distinct().Count()}个目标；属于人工丢弃，不是发送成功。"),
            "delivery.cleanupOutcomeUnknown", operation, token);
    }

    public Task<DeliveryWriteResult> ClearPendingDeliveriesAsync(long connectionId, long revision,
        string account, string? sourceIp, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(account);
        if (connectionId is < 1 or > 9007199254740991L || revision < 1)
        { throw new ArgumentException("清空目标或有效版本无效。"); }
        TaskCompletionSource operation;
        lock (_lock)
        {
            if (_stopping is not null || _application is not null || _deliveryWrite is not null ||
                _state is not (RuntimeCoordinatorState.Active or RuntimeCoordinatorState.RunFailed) || _activeRevision != revision)
            { throw new DeliveryClearPendingUnavailableException(); }
            operation = NewSignal();
            _deliveryWrite = operation;
        }
        return OwnDeliveryWrite(() => runtime.ClearPendingAsync(connectionId, revision, CancellationToken.None),
            result => new("delivery.clearPending", AuditResults.Success, account, sourceIp,
                connectionId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                $"在有效版本r{revision}下清空执行边界时仍待发的{result.AffectedRecords}条记录；人工丢弃，非发送确认。"),
            "delivery.clearPendingOutcomeUnknown", operation, token);
    }

    private Task<DeliveryWriteResult> OwnDeliveryWrite(Func<Task<DeliveryWriteResult>> write, Func<DeliveryWriteResult, AuditEntry> success,
        string unknownCode, TaskCompletionSource completion, CancellationToken token)
    {
        Task<DeliveryWriteResult> owned;
        using (ExecutionContext.SuppressFlow())
        { owned = Task.Run(() => RunDeliveryWriteAsync(write, success, unknownCode, completion)); }
        return owned.WaitAsync(token);
    }

    private async Task<DeliveryWriteResult> RunDeliveryWriteAsync(Func<Task<DeliveryWriteResult>> write, Func<DeliveryWriteResult, AuditEntry> success,
        string unknownCode, TaskCompletionSource completion)
    {
        var failure = success(new(DeliveryWriteStatus.Rejected, [], 0, unknownCode)) with { Result = AuditResults.Failure, Detail = "队列管理操作未完成或结果不可确定，请重新读取；不自动重放。" };
        try
        {
            var result = await write();
            audit.AppendOrLog(result.Committed ? success(result) : failure);
            return result;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            audit.AppendOrLog(failure);
            return new(DeliveryWriteStatus.StorageFailed, [], 0, unknownCode);
        }
        finally
        {
            lock (_lock)
            {
                _deliveryWrite = null;
                completion.TrySetResult();
            }
        }
    }
}
