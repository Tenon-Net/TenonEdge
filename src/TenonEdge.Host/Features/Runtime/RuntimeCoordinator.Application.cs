using TenonEdge.Host.Features.Audit;
using TenonEdge.Host.Features.Routes;
using TenonEdge.Runtime.Lifecycle;

namespace TenonEdge.Host.Features.Runtime;

internal sealed record RuntimeApplicationResult(long Revision, bool Committed, bool Activated, string Code, bool RecoveryPending = false, bool? DraftReplaced = null);
internal sealed class RuntimeApplicationBusyException(RuntimeApplicationStatus application) : InvalidOperationException
{
    public RuntimeApplicationStatus Application { get; } = application;
}

internal sealed partial class RuntimeCoordinator
{
    private TaskCompletionSource? _application;

    /// <summary>拥有完整在线应用操作；只接收确认过的草稿版本及已解析的操作人，不接收 HttpContext 或任意运行计划。
    /// 取消只结束调用方等待。冻结之后，即使请求断开也完成提交或放弃/恢复；停机先等本操作结束。</summary>
    public Task<RuntimeApplicationResult> ApplyDraftAsync(string expectedDraftVersion, string account, string? sourceIp, CancellationToken cancellationToken) =>
        StartApplication(null, expectedDraftVersion, account, sourceIp, cancellationToken);

    public Task<RuntimeApplicationResult> RollbackAsync(long targetRevision, string expectedDraftVersion, string account, string? sourceIp, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetRevision);
        return StartApplication(targetRevision, expectedDraftVersion, account, sourceIp, cancellationToken);
    }

    private Task<RuntimeApplicationResult> StartApplication(long? targetRevision, string expectedDraftVersion, string account, string? sourceIp, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(account);
        if (string.IsNullOrWhiteSpace(expectedDraftVersion) || expectedDraftVersion.Length > 64)
        { throw new ArgumentException("须提供有效草稿版本。", nameof(expectedDraftVersion)); }
        TaskCompletionSource operation;
        var operationId = Guid.NewGuid();
        lock (_lock)
        {
            if (_stopping is not null || _state is RuntimeCoordinatorState.Starting or RuntimeCoordinatorState.Blocked or RuntimeCoordinatorState.Stopping or RuntimeCoordinatorState.Stopped)
            { throw new InvalidOperationException("当前运行状态不允许应用配置。"); }
            if (_application is not null)
            { throw new RuntimeApplicationBusyException(_applications[^1]); }
            operation = NewSignal();
            _applications.Add(new(operationId, targetRevision is null ? "apply" : "rollback", account, expectedDraftVersion, targetRevision,
                null, RuntimeApplicationStage.Validating, time.GetUtcNow(), null, false, null, null));
            if (_applications.Count > MaximumRecentApplications)
            { _applications.RemoveAt(0); }
            _application = operation;
        }
        Task<ApplicationOutcome> task;
        // 不把请求的 AsyncLocal/HttpContextAccessor 或数据库客户端上下文带入所拥有的后台流程。
        using (ExecutionContext.SuppressFlow())
        { task = Task.Run(() => RunApplicationAsync(targetRevision, expectedDraftVersion, account, sourceIp, operationId, operation)); }
        return AwaitApplicationAsync(task, cancellationToken);
    }

    private async Task<RuntimeApplicationResult> ExecuteApplicationAsync(long? targetRevision, string expectedDraftVersion, string account, string? sourceIp, Guid operationId)
    {
        RuntimePlanDocument document;
        long revision;
        if (targetRevision is { } target)
        {
            var rollback = versions.FreezeManualRollback(target, expectedDraftVersion, account);
            document = rollback.Document;
            revision = rollback.Revision;
        }
        else
        {
            var snapshot = drafts.ReadCompilation(expectedDraftVersion, CancellationToken.None);
            document = RoutePlanCompiler.Compile(snapshot, CancellationToken.None).Document;
            revision = versions.FreezeDraft(document, expectedDraftVersion, account);
        }
        AdvanceApplication(operationId, RuntimeApplicationStage.Preparing, revision);
        PreparedPlan? prepared = null;
        var quiesced = false;
        var committed = false;
        bool? draftReplaced = null;
        var code = "runtime.prepareFailed";
        try
        {
            prepared = runtime.Prepare(document.ToPlan(revision));
            if (prepared.Failures.Count != 0)
            { throw new ApplicationRejectedException(code); }
            code = "runtime.targetChangeBlocked";
            if (versions.TargetChangeProblem(document) is not null)
            { throw new ApplicationRejectedException(code); }
            code = "runtime.quiescenceFailed";
            AdvanceApplication(operationId, RuntimeApplicationStage.Quiescing, revision);
            quiesced = true;
            var quiet = await runtime.QuiesceInputsAsync(CancellationToken.None);
            if (!quiet.Succeeded || quiet.Revision is not null && !runtime.IsCurrentQuiescence(quiet))
            { throw new ApplicationRejectedException(code); }
            code = "runtime.targetChangeBlocked";
            if (versions.TargetChangeProblem(document) is not null)
            { throw new ApplicationRejectedException(code); }
            code = "runtime.commitFailed";
            AdvanceApplication(operationId, RuntimeApplicationStage.Committing, revision, committed: null);
            var replaced = versions.Commit(revision, new AuditEntry(targetRevision is null ? AuditActions.SnapshotApplied : AuditActions.SnapshotRolledBack,
                AuditResults.Success, account, sourceIp, Target(revision), targetRevision is null
                    ? $"应用已确认的草稿版本 {expectedDraftVersion}。" : $"回滚到版本 r{targetRevision}，生成新版本；已发送数据不撤回。"),
                markRunning: true, replaceDraftVersion: targetRevision is null ? null : expectedDraftVersion);
            draftReplaced = targetRevision is null ? null : replaced;
            committed = true;
            lock (_lock)
            {
                _activeRevision = revision;
                _activeIsStable = false;
                SignalVersionChange();
            }
            AdvanceApplication(operationId, RuntimeApplicationStage.Activating, revision, committed: true);
            if (quiet.Revision is null)
            { await runtime.ActivateAsync(prepared); }
            else
            { await runtime.ActivateCommittedAsync(prepared, quiet); }
            lock (_lock)
            {
                _state = RuntimeCoordinatorState.Active;
                _reason = null;
                ResetStabilityClock();
            }
            return new(revision, true, true, targetRevision is null ? "runtime.applied" : "runtime.rolledBack", DraftReplaced: draftReplaced);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (!committed && code == "runtime.commitFailed")
            {
                // 存储异常不能单凭方法是否返回推断提交结果；无法读回时保持静默，交给修复/重启处理。
                try
                { committed = versions.ReadPointers().EffectiveRevision == revision; }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    Fail("无法确认配置提交结果，保持输入静默；请修复存储后重启，不恢复可能已过期的版本。");
                    throw;
                }
            }
            if (committed)
            {
                lock (_lock)
                {
                    _activeRevision = revision;
                    _activeIsStable = false;
                }
                Fail("新版本已提交，但运行时激活未完成；请检查运行单元，不自动回滚。");
                return new(revision, true, false, "runtime.activationFailed", DraftReplaced: draftReplaced);
            }
            if (exception is ApplicationRejectedException rejected)
            { code = rejected.Code; }
            try
            {
                var reason = code == "runtime.prepareFailed" && prepared is not null
                    ? Truncate($"{code}：{Describe(prepared)}") : code;
                versions.Abandon(revision, reason);
            }
            catch (Exception error) when (IsStorageError(error))
            {
                runtimeEvents.Record(RuntimeEventLevel.Error, RuntimeEventTypes.VersionNotEffective, Target(revision),
                    "放弃版本未能写入配置库，保留 pending 供重启恢复；未提交新有效版本。");
            }
            RuntimeInputRecoveryResult? recovery = null;
            if (quiesced)
            {
                AdvanceApplication(operationId, RuntimeApplicationStage.Restoring, revision);
                recovery = await runtime.RestoreInputsAsync();
                lock (_lock)
                { ResetStabilityClock(); }
            }
            runtimeEvents.Record(RuntimeEventLevel.Warning, RuntimeEventTypes.VersionNotEffective, Target(revision),
                "本次应用未提交，原有效版本保持不变；输入恢复状态按运行单元显示。");
            return new(revision, false, false, code, recovery is { Succeeded: false });
        }
        finally
        {
            if (prepared is not null)
            { await prepared.DisposeAsync(); }
        }
    }

    private sealed class ApplicationRejectedException(string code) : Exception
    {
        public string Code { get; } = code;
    }
}
