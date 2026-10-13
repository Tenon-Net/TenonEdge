using TenonEdge.Host.Features.Audit;
using TenonEdge.Host.Features.Deliveries;
using TenonEdge.Runtime.Deliveries;

namespace TenonEdge.Host.Features.Runtime;

internal sealed class DeliveryMigrationUnavailableException : InvalidOperationException;

internal sealed partial class RuntimeCoordinator
{
    /// <summary>仅供已经管理员授权、准备并确认的选择调用。HTTP 生命周期不拥有执行；
    /// 与应用/回滚/其他队列写入互斥，开始记录先持久化，最终结果以队列收据同步，停机等待两库同步尝试结束。</summary>
    public Task<DeliveryWriteResult> MigrateSelectedDeliveriesAsync(DeliveryMigrationCommand command, long revision,
        string sourceSummary, bool sameOwnerConfirmed, string account, string? sourceIp, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceSummary);
        ArgumentException.ThrowIfNullOrWhiteSpace(account);
        if (revision < 1 || !sameOwnerConfirmed || command.Items is null)
        { throw new ArgumentException("须提供已确认同一所有者的明确迁移选择。"); }
        TaskCompletionSource operation;
        lock (_lock)
        {
            if (_stopping is not null || _application is not null || _deliveryWrite is not null ||
                _state is not (RuntimeCoordinatorState.Active or RuntimeCoordinatorState.RunFailed) || _activeRevision != revision)
            { throw new DeliveryMigrationUnavailableException(); }
            operation = NewSignal();
            _deliveryWrite = operation;
        }
        Task<DeliveryWriteResult> owned;
        using (ExecutionContext.SuppressFlow())
        { owned = Task.Run(() => RunMigrationOwnedAsync(command, revision, sourceSummary, account, sourceIp, operation)); }
        return owned.WaitAsync(token);
    }

    private async Task<DeliveryWriteResult> RunMigrationOwnedAsync(DeliveryMigrationCommand command, long revision,
        string sourceSummary, string account, string? sourceIp, TaskCompletionSource completion,
        DeliveryMigrationIntentRecord? intent = null, string? validationError = null)
    {
        var started = false;
        try
        {
            if (intent is null)
            { migrations.Begin(command, revision, sourceSummary, true, account, sourceIp); }
            else
            { migrations.BeginIntent(command, revision); }
            started = true;
            DeliveryWriteResult result;
            try
            {
                result = validationError is not null ? new(DeliveryWriteStatus.Rejected, [], 0, validationError)
                : await runtime.MigrateDeliveriesAsync(command, revision, CancellationToken.None);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            { result = new(DeliveryWriteStatus.StorageFailed, [], 0, "delivery.migrationExecutionFailed"); }
            var finished = migrations.Finish(command.OperationId, result);
            if (intent is not null)
            {
                runtimeEvents.Record(finished.Status == "completed" ? RuntimeEventLevel.Info : RuntimeEventLevel.Warning,
                    finished.Status == "completed" ? "delivery.migrationIntentCompleted" : "delivery.migrationIntentRejected", intent.IntentId,
                    finished.Status == "completed" ? $"意图已迁移 {finished.AffectedRecords} 条，不代表已发送。"
                        : $"意图未执行（{finished.ErrorCode}）；记录保持隔离，须管理员核对后处理。");
            }
            if (finished.Status != "completed")
            { return result; }
            // 即使上层等待报告错误，已经持久化的 queue.db 结果仍优先；不要把实际成功写成“未执行”。
            return new(DeliveryWriteStatus.Committed, [], finished.AffectedRecords!.Value, null)
            {
                Migration = new(finished.MigrationId!.Value, command.OperationId, finished.ConfigStoreId, finished.RequestHash,
                    finished.SourceConnectionId, finished.TargetConnectionId, finished.AffectedRecords.Value,
                    DateTimeOffset.FromUnixTimeMilliseconds(finished.CompletedAt!.Value)),
            };
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            audit.AppendOrLog(new(started ? "delivery.migrationSyncFailed" : "delivery.migrationStartFailed", AuditResults.Failure,
                account, sourceIp, command.OperationId.ToString("D"), started
                    ? "结果同步未完成，启动时须按队列完成记录核对；不能重放或断言未执行。"
                    : "迁移开始未能持久登记，未交给队列执行。"));
            return new(DeliveryWriteStatus.StorageFailed, [], 0, "delivery.migrationOutcomeUnknown");
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
