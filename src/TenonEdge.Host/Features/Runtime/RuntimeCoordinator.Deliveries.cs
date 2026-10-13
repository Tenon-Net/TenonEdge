using TenonEdge.Host.Features.Audit;
using TenonEdge.Runtime.Deliveries;

namespace TenonEdge.Host.Features.Runtime;

internal sealed class DeliveryRetryUnavailableException : InvalidOperationException;

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
        Task<DeliveryWriteResult> owned;
        using (ExecutionContext.SuppressFlow())
        { owned = Task.Run(() => RunDeliveryRetryAsync(selected, targets, revision, account, sourceIp, operation)); }
        return owned.WaitAsync(token);
    }

    private async Task<DeliveryWriteResult> RunDeliveryRetryAsync(DeliveryRetryItem[] selected,
        IReadOnlyDictionary<long, string> targets, long revision, string account, string? sourceIp, TaskCompletionSource completion)
    {
        try
        {
            var result = await runtime.Deliveries.RetryFailedAsync(selected, targets, CancellationToken.None);
            audit.AppendOrLog(new("delivery.retryFailed", result.Committed ? AuditResults.Success : AuditResults.Failure,
                account, sourceIp, Target(revision), result.Committed
                    ? $"已将{result.AffectedRecords}条失败记录移至队尾；保留投递编号，可能重复，不代表已发送。"
                    : "重试失败记录未完成或结果不可确定；请重新读取，不自动重放。"));
            return result;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            audit.AppendOrLog(new("delivery.retryFailed", AuditResults.Failure, account, sourceIp, Target(revision),
                "无法确定失败记录重试结果，请重新读取记录，不自动重放。"));
            return new(DeliveryWriteStatus.StorageFailed, [], 0, "delivery.retryOutcomeUnknown");
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
