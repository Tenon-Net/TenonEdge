using TenonEdge.Host.Features.Audit;
using TenonEdge.Runtime.Lifecycle;

namespace TenonEdge.Host.Features.Runtime;

internal sealed partial class RuntimeCoordinator
{
    public RuntimeApplicationStatus BeginRetry(long revision, string planHash, AuditAccount account, string? sourceIp, CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(revision);
        ArgumentException.ThrowIfNullOrWhiteSpace(account.Name);
        return StartOperation("retry", "", revision, account, id => RetryAsync(revision, planHash, account, sourceIp, id), token).Status;
    }

    private async Task<RuntimeApplicationResult> RetryAsync(long revision, string planHash, AuditAccount account, string? sourceIp, Guid operationId)
    {
        var checkpoint = versions.ReadRetryCheckpoint(revision, planHash);
        PreparedPlan? prepared = null;
        try
        {
            versions.MarkRetry(checkpoint, account, sourceIp);
            lock (_lock)
            { _activeRevision = revision; _activeIsStable = checkpoint.Snapshot.IsStable; }
            AdvanceApplication(operationId, RuntimeApplicationStage.Preparing, revision);
            prepared = runtime.Prepare(checkpoint.Plan.ToPlan(revision));
            // 与启动加载一致：单元结构性故障只降级该单元；不恢复历史凭证，也不假装健康。
            AdvanceApplication(operationId, RuntimeApplicationStage.Quiescing, revision);
            var quiet = await runtime.QuiesceInputsAsync(CancellationToken.None);
            if (!quiet.Succeeded || quiet.Revision is not null && !runtime.IsCurrentQuiescence(quiet))
            {
                Fail("人工重试未完成输入静默；保留未退出实例，需检查运行状态或重启服务。");
                return new(revision, false, false, "runtime.quiescenceFailed", RecoveryPending: true);
            }
            // 准备/静默期间配置存储被修复或变化时，不激活旧的检查结果。
            _ = versions.ReadRetryCheckpoint(revision, planHash);
            await HoldWaitingMigrationIntentsAsync(quiet);
            AdvanceApplication(operationId, RuntimeApplicationStage.Activating, revision);
            if (quiet.Revision is null)
            { await runtime.ActivateAsync(prepared); }
            else
            { await runtime.RetryEffectiveAsync(prepared, quiet); }
            lock (_lock)
            {
                _state = RuntimeCoordinatorState.Active;
                _reason = null;
                ResetStabilityClock();
                SignalVersionChange();
            }
            runtimeEvents.Record(RuntimeEventLevel.Info, RuntimeEventTypes.RuntimeRetried, Target(revision),
                "人工重试已激活当前有效版本；未应用草稿或生成版本，单元健康情况按运行诊断显示。");
            return new(revision, false, true, "runtime.retried");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Fail("人工重试未能激活当前有效版本；请检查配置、存储与运行单元，不自动重放操作。");
            return new(revision, false, false, ApplicationErrorCode(exception));
        }
        finally
        {
            if (prepared is not null)
            { await prepared.DisposeAsync(); }
        }
    }
}
