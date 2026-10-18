using Microsoft.Data.Sqlite;
using SqlSugar;
using TenonEdge.Host.Bootstrap;
using TenonEdge.Host.Features.Audit;
using TenonEdge.Host.Features.Deliveries;
using TenonEdge.Host.Features.Routes;
using TenonEdge.Host.Storage;
using TenonEdge.Runtime.Deliveries;
using TenonEdge.Runtime.Lifecycle;

namespace TenonEdge.Host.Features.Runtime;

/// <summary>
/// 运行时协调者（总设计 §7.4、§8.1，T1-01）：进程中唯一的运行时后台服务，决定激活哪个版本，并维护配置库中的版本元数据。
/// 启动时（宿主开始接收请求之前完成）依次：
/// <list type="number">
/// <item>运行时启动条件不满足（SQLite 版本过低、容器中数据目录不在持久卷上）时什么都不启动。</item>
/// <item>启动恢复：丢弃上次提交前中断的 pendingRevision，标为“未生效：进程中断”。</item>
/// <item>启动保护“先检查、后计数”：上次的“运行中”标记仍在，且该版本从未稳定，则非正常退出次数加一。</item>
/// <item>种子快照内容有变化时导入：冻结为新版本、只构建不启动，有构建失败即放弃（原有效版本不受影响），否则提交为有效版本。</item>
/// <item>核对有效快照的内容哈希（损坏即“运行失败”，不静默清空配置），按启动保护判定：照常激活、自动回滚一次，或“运行失败”。</item>
/// <item>加载有效版本时先落盘“运行中”标记，再构建它的运行单元：构建或激活过程中崩溃都计为非正常退出。
/// 新导入或自动回滚生成的版本在提交之后、激活之前落盘；它们在提交之前的构建中崩溃不计入启动保护，由下面的中断上限处理。</item>
/// <item>启动发送缓存写者（“运行失败”时也启动，输出继续清空队列），然后启动输入。</item>
/// </list>
/// 导入种子或自动回滚在提交前连续中断（例如构建运行单元时进程崩溃）达到上限后不再尝试，避免崩溃循环与快照无限增长；
/// 被封顶的种子只有内容（规范化之后）改变才会再次导入。
/// 之后在后台计时，连续运行满稳定时间把版本记为“已稳定”。停止时按停机顺序停止运行时；只有正常停止才清除“运行中”标记，
/// 运行时宿主异常结束（后台服务故障）时保留标记，下次启动计为非正常退出。
/// </summary>
internal sealed partial class RuntimeCoordinator(
    RuntimeHost runtime,
    RuntimeVersionStore versions,
    RouteDraftStore drafts,
    RuntimeSettings settings,
    RuntimeStartConditions startConditions,
    RuntimeEventLog runtimeEvents,
    AuditTrail audit,
    DeliveryMigrationStore migrations,
    TimeProvider time,
    ILogger<RuntimeCoordinator> logger) : BackgroundService
{
    private const int MaximumReasonLength = 2000;

    private readonly Lock _lock = new();
    private RuntimeCoordinatorState _state = RuntimeCoordinatorState.Starting;
    private string? _reason;
    private long? _activeRevision;
    private bool _activeIsStable;
    private Task? _stopping;

    public RuntimeCoordinatorStatus Status
    {
        get
        {
            lock (_lock)
            {
                return new RuntimeCoordinatorStatus(_state, _reason, _activeRevision, _deliveryWrite is not null);
            }
        }
    }

    public RuntimeHost Runtime => runtime;

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!startConditions.AllowsRuntimeStart)
        {
            SetState(RuntimeCoordinatorState.Blocked, "运行时启动条件不满足，原因见运行事件。");
            LogBlocked(logger);
        }
        else
        {
            await StartRuntimeAsync();
        }

        await base.StartAsync(cancellationToken);
    }

    /// <summary>
    /// 按停机顺序停止运行时，正常停止时清除“运行中”标记。重复或并发的调用等待同一次停止，标记只在这一次停止中清除。
    /// </summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        TaskCompletionSource? owner = null;
        Task stopping;
        lock (_lock)
        {
            if (_stopping is null)
            {
                owner = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _stopping = owner.Task;
            }

            stopping = _stopping;
        }

        if (owner is null)
        {
            await stopping;
            return;
        }

        try
        {
            await StopOnceAsync(cancellationToken);
            owner.SetResult();
        }
        catch (Exception exception)
        {
            owner.SetException(exception);
            throw;
        }
    }

    private async Task StopOnceAsync(CancellationToken cancellationToken)
    {
        Task application;
        lock (_lock)
        { application = Task.WhenAll(_application?.Task ?? Task.CompletedTask, _deliveryWrite?.Task ?? Task.CompletedTask); }
        await application;
        await base.StopAsync(cancellationToken);
        var faulted = ExecuteTask is { IsFaulted: true };
        long? revision;
        lock (_lock)
        {
            revision = _activeRevision;
            _state = RuntimeCoordinatorState.Stopping;
            CancelStabilityTimer();
        }

        try
        {
            await runtime.StopAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // 运行时的停机步骤各自捕获异常，这里只是兜底：仍然按正常停止清除标记。
            LogRuntimeStopFailed(logger, exception);
        }

        if (revision is { } active && !faulted)
        {
            try
            {
                versions.ClearRunningMarker(active);
            }
            catch (Exception exception) when (IsStorageError(exception))
            {
                LogMarkerNotCleared(logger, active);
            }
        }

        SetState(RuntimeCoordinatorState.Stopped, null);
    }

    private static string Target(long revision) => $"r{revision}";

    private static bool IsStorageError(Exception exception) => exception is SqliteException or SqlSugarException or DeliveryStoreException;

    private static string Truncate(string reason) => reason.Length > MaximumReasonLength ? reason[..MaximumReasonLength] : reason;

    private async Task StartRuntimeAsync()
    {
        StartupDecision decision;
        try
        {
            // 核对发生在启动任何发送者之前；仅补记结果，不重放上次已开始的人工操作。
            migrations.RecoverInterrupted();
            await HoldWaitingMigrationIntentsAsync();
            decision = await EvaluateAsync();
        }
        catch (Exception exception) when (IsStorageError(exception))
        {
            decision = StartupDecision.Fail("读写配置库中的版本信息失败，请检查存储状态。");
            LogStorageFailure(logger);
        }

        try
        {
            runtime.StartCore();
            if (decision.Prepared is null && decision.FailureReason is null && versions.ReadPointers().EffectiveRevision is null)
            {
                await runtime.QuarantineUnconfiguredTargetsAsync(CancellationToken.None);
            }

            if (decision.Prepared is { } prepared)
            {
                await runtime.ActivateAsync(prepared);
                lock (_lock)
                {
                    _state = RuntimeCoordinatorState.Active;
                    _reason = null;
                    _activeRevision = prepared.Revision;
                    _activeIsStable = decision.ActiveIsStable;
                    ResetStabilityClock();
                }

                return;
            }
        }
        catch (DeliveryStoreException exception)
        {
            Fail(exception.Message);
            return;
        }

        if (decision.FailureReason is { } reason)
        {
            Fail(reason);
            return;
        }

        SetState(RuntimeCoordinatorState.Idle, null);
        LogIdle(logger);
    }

    private async Task<StartupDecision> EvaluateAsync()
    {
        if (versions.DiscardInterruptedPending() is { } interrupted)
        {
            LogInterruptedPending(logger, interrupted);
            runtimeEvents.Record(
                RuntimeEventLevel.Warning,
                RuntimeEventTypes.VersionNotEffective,
                Target(interrupted),
                $"版本 r{interrupted} 在提交前中断，标为未生效：进程中断。");
        }

        if (versions.ConsumeRunningMarker() is { } marker)
        {
            LogAbnormalExit(logger, marker.Revision, marker.Counted, marker.AbnormalExitCount);
            runtimeEvents.Record(
                marker.Counted ? RuntimeEventLevel.Warning : RuntimeEventLevel.Info,
                RuntimeEventTypes.AbnormalExit,
                Target(marker.Revision),
                marker.Counted
                    ? $"上次运行版本 r{marker.Revision} 时进程非正常退出；该版本从未稳定运行，累计 {marker.AbnormalExitCount} 次（达到 {StartupProtection.AbnormalExitLimit} 次触发启动保护）。"
                    : $"上次运行版本 r{marker.Revision} 时进程非正常退出；该版本已稳定运行过，不计入启动保护。");
        }

        var seeded = await TryImportSeedAsync();
        var pointers = versions.ReadPointers();
        if (pointers.EffectiveRevision is not { } effectiveRevision)
        {
            return StartupDecision.Idle;
        }

        var effective = versions.Load(effectiveRevision);
        var problem = "快照不存在";
        RuntimePlanDocument? document = null;
        if (effective is null || !TryReadDocument(effective, out document, out problem))
        {
            await DisposeAsync(seeded);
            return StartupDecision.Fail($"有效配置 r{effectiveRevision} 已损坏，不启动输入：{problem}。请回滚或从备份恢复。");
        }

        // 已生效快照由原 Prepare 逐单元校验：输入或转换模块缺失/不兼容，仅降级
        // 受影响的输入/输出单元，不绕过转换，也不拖停其他单元。新冻结/回滚仍严格准入。

        var previous = pointers.PreviousRevision is { } previousRevision ? versions.Load(previousRevision) : null;
        var protection = StartupProtection.Decide(effective, previous);
        switch (protection.Action)
        {
            case StartupProtectionAction.Activate:
                if (seeded?.Revision == effectiveRevision)
                {
                    return await ReadyToActivateAsync(seeded, effective.IsStable);
                }

                await DisposeAsync(seeded);

                // 先落盘标记，再构建：构建运行单元时进程崩溃同样计为该版本的非正常退出。
                versions.SetRunningMarker(effectiveRevision);
                return StartupDecision.Activate(runtime.Prepare(document!.ToPlan(effectiveRevision)), effective.IsStable);
            case StartupProtectionAction.RollBack:
                await DisposeAsync(seeded);
                return await RollBackAsync(previous!, protection.Reason!);
            default:
                await DisposeAsync(seeded);
                return StartupDecision.Fail(protection.Reason!);
        }
    }

    /// <summary>启动保护的自动回滚（总设计 §7.4）：内容与上一生效版本相同的新版本，操作人为“系统”，不改动草稿；记审计与运行事件。</summary>
    private async Task<StartupDecision> RollBackAsync(RuntimeSnapshotRecord target, string reason)
    {
        if (!TryReadDocument(target, out var document, out var problem))
        {
            return StartupDecision.Fail($"{reason}但上一生效版本 r{target.Revision} 的快照已损坏：{problem}。");
        }

        try
        { document.ValidateTransforms(runtime.Transforms); }
        catch (RuntimePlanDocumentException)
        { return StartupDecision.Fail("上一版本需要的转换模块不可用或配置无效，不能自动回滚。"); }

        try
        { document.ValidateInputs(runtime.InputModules); }
        catch (RuntimePlanDocumentException)
        { return StartupDecision.Fail("上一版本需要的输入模块不可用或配置无效，不能自动回滚。"); }

        var interrupted = versions.CountInterrupted(RuntimeSnapshotOrigins.AutoRollback, target.ContentHash, Target(target.Revision));
        if (interrupted >= StartupProtection.AbnormalExitLimit)
        {
            return StartupDecision.Fail($"{reason}但自动回滚到 r{target.Revision} 已有 {interrupted} 次在提交前中断（例如构建运行单元时进程崩溃），不再尝试。");
        }

        var revision = versions.FreezeRollback(target, RuntimeVersionStore.SystemOperator);
        var prepared = runtime.Prepare(document.ToPlan(revision));
        var targetChangeProblem = prepared.Failures.Count == 0 ? versions.TargetChangeProblem(document) : null;
        if (prepared.Failures.Count > 0 || targetChangeProblem is not null)
        {
            await prepared.DisposeAsync();
            var failure = Truncate($"自动回滚生成的版本 r{revision} 准备失败：{targetChangeProblem ?? Describe(prepared)}");
            versions.Abandon(revision, failure);
            return StartupDecision.Fail($"{reason}{failure}");
        }

        var detail = Truncate($"{reason}新版本 r{revision}。");
        versions.Commit(
            revision,
            new AuditEntry(AuditActions.AutoRollback, AuditResults.Success, RuntimeVersionStore.SystemOperator, SourceIp: null, Target(revision), detail));
        LogAutoRollback(logger, target.Revision, revision, reason);
        runtimeEvents.Record(RuntimeEventLevel.Warning, RuntimeEventTypes.AutoRollback, Target(revision), $"启动保护：已自动回滚。{detail}");
        return await ReadyToActivateAsync(prepared, activeIsStable: false);
    }

    /// <summary>种子快照的内容与上次导入的不同时，按“冻结、准备、提交”导入为新的有效版本；返回已构建、尚未激活的计划。</summary>
    private async Task<PreparedPlan?> TryImportSeedAsync()
    {
        if (settings.SeedSnapshotPath is not { } path)
        {
            return null;
        }

        RuntimePlanDocument document;
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists)
            {
                throw new RuntimePlanDocumentException("文件不存在。");
            }

            if (file.Length > RuntimePlanDocument.MaximumBytes)
            {
                throw new RuntimePlanDocumentException($"文件超过 {RuntimePlanDocument.MaximumBytes / 1024 / 1024} MiB。");
            }

            document = RuntimePlanDocument.Parse(await File.ReadAllBytesAsync(path), runtime.Transforms, runtime.InputModules);
            if (versions.ReadPointers().SeedHash != document.ContentHash)
            { versions.CheckNewPlanLimits(document); }
        }
        catch (Exception exception) when (exception is RuntimePlanDocumentException or TenonEdge.Host.Features.Routes.RouteTopologyException or IOException or UnauthorizedAccessException)
        {
            LogSeedRejected(logger, path, exception.Message);
            runtimeEvents.Record(RuntimeEventLevel.Error, RuntimeEventTypes.SeedRejected, path, Truncate($"种子快照无效，没有导入：{exception.Message}"));
            return null;
        }

        if (versions.ReadPointers().SeedHash == document.ContentHash)
        {
            LogSeedUnchanged(logger, path);
            return null;
        }

        var interrupted = versions.CountInterrupted(RuntimeSnapshotOrigins.Seed, document.ContentHash, originDetail: null);
        if (interrupted >= StartupProtection.AbnormalExitLimit)
        {
            // 导入这份种子时进程已反复在提交前退出（例如构建运行单元时崩溃）：不再尝试，原有效版本不变。
            versions.RecordSeedHash(document.ContentHash);
            var reason = $"种子快照的导入已有 {interrupted} 次在提交前中断（例如构建运行单元时进程崩溃），不再导入这份种子；原有效版本不变。";
            LogSeedRejected(logger, path, reason);
            runtimeEvents.Record(RuntimeEventLevel.Error, RuntimeEventTypes.SeedRejected, path, reason);
            return null;
        }

        var revision = versions.Freeze(document, RuntimeSnapshotOrigins.Seed, document.ContentHash, RuntimeVersionStore.SystemOperator);
        var prepared = runtime.Prepare(document.ToPlan(revision));
        var targetChangeProblem = prepared.Failures.Count == 0 ? versions.TargetChangeProblem(document) : null;
        if (prepared.Failures.Count > 0 || targetChangeProblem is not null)
        {
            await prepared.DisposeAsync();
            var reason = Truncate($"准备阶段失败：{targetChangeProblem ?? Describe(prepared)}");
            versions.Abandon(revision, reason, document.ContentHash);
            LogSeedNotEffective(logger, revision, reason);
            runtimeEvents.Record(RuntimeEventLevel.Error, RuntimeEventTypes.VersionNotEffective, Target(revision), $"种子快照生成的版本 r{revision} 未生效，原有效版本不变。{reason}");
            return null;
        }

        versions.Commit(
            revision,
            new AuditEntry(
                AuditActions.SeedSnapshotApplied,
                AuditResults.Success,
                RuntimeVersionStore.SystemOperator,
                SourceIp: null,
                Target(revision),
                Truncate($"种子快照 {path}，内容哈希 {document.ContentHash}。")),
            document.ContentHash);
        LogSeedApplied(logger, path, revision);
        runtimeEvents.Record(RuntimeEventLevel.Info, RuntimeEventTypes.SeedApplied, Target(revision), $"种子快照已生效：版本 r{revision}。");
        return prepared;
    }

    /// <summary>新导入或自动回滚生成的版本已经提交：激活之前落盘“运行中”标记。写入失败时释放已构建的计划。</summary>
    private async Task<StartupDecision> ReadyToActivateAsync(PreparedPlan prepared, bool activeIsStable)
    {
        try
        {
            versions.SetRunningMarker(prepared.Revision);
        }
        catch
        {
            await prepared.DisposeAsync();
            throw;
        }

        return StartupDecision.Activate(prepared, activeIsStable);
    }

    private static bool TryReadDocument(RuntimeSnapshotRecord snapshot, out RuntimePlanDocument document, out string problem)
    {
        document = null!;
        try
        {
            document = RuntimeSnapshotDocument.ReadExecutionPlan(snapshot);
        }
        catch (RuntimePlanDocumentException exception)
        {
            problem = exception.Message;
            return false;
        }

        problem = string.Empty;
        return true;
    }

    private static string Describe(PreparedPlan prepared) =>
        string.Join("；", prepared.Failures.Select(failure => $"{failure.Id}：{failure.Reason}"));

    private static async Task DisposeAsync(PreparedPlan? prepared)
    {
        if (prepared is not null)
        {
            await prepared.DisposeAsync();
        }
    }

    private void Fail(string reason)
    {
        var truncated = Truncate(reason);
        SetState(RuntimeCoordinatorState.RunFailed, truncated);
        LogRunFailed(logger, truncated);
        runtimeEvents.Record(RuntimeEventLevel.Error, RuntimeEventTypes.RunFailed, target: null, $"运行失败，输入不启动，发送缓存照常运行：{truncated}");
    }

    private void SetState(RuntimeCoordinatorState state, string? reason)
    {
        lock (_lock)
        {
            _state = state;
            _reason = reason;
            if (state != RuntimeCoordinatorState.Active)
            { CancelStabilityTimer(); }
            SignalVersionChange();
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "运行时启动条件不满足，运行时不启动；管理界面照常可用。")]
    private static partial void LogBlocked(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "尚无有效配置，运行时只启动发送缓存。")]
    private static partial void LogIdle(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "版本 r{Revision} 在提交前中断，标为未生效。")]
    private static partial void LogInterruptedPending(ILogger logger, long revision);

    [LoggerMessage(Level = LogLevel.Warning, Message = "上次运行版本 r{Revision} 时进程非正常退出；计入启动保护：{Counted}，累计 {Count} 次。")]
    private static partial void LogAbnormalExit(ILogger logger, long revision, bool counted, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "种子快照 {Path} 无效，没有导入：{Reason}")]
    private static partial void LogSeedRejected(ILogger logger, string path, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "种子快照 {Path} 与上次导入的内容相同，不再导入。")]
    private static partial void LogSeedUnchanged(ILogger logger, string path);

    [LoggerMessage(Level = LogLevel.Error, Message = "种子快照生成的版本 r{Revision} 未生效，原有效版本不变：{Reason}")]
    private static partial void LogSeedNotEffective(ILogger logger, long revision, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "种子快照 {Path} 已导入为有效版本 r{Revision}。")]
    private static partial void LogSeedApplied(ILogger logger, string path, long revision);

    [LoggerMessage(Level = LogLevel.Warning, Message = "启动保护：从版本 r{From} 的内容生成 r{To} 并自动回滚。{Reason}")]
    private static partial void LogAutoRollback(ILogger logger, long from, long to, string reason);

    [LoggerMessage(Level = LogLevel.Critical, Message = "运行失败，输入不启动，发送缓存照常运行：{Reason}")]
    private static partial void LogRunFailed(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "版本 r{Revision} 已连续运行 {StableAfter}，记为已稳定。")]
    private static partial void LogStable(ILogger logger, long revision, TimeSpan stableAfter);

    [LoggerMessage(Level = LogLevel.Warning, Message = "未能把版本 r{Revision} 记为已稳定。")]
    private static partial void LogStableNotRecorded(ILogger logger, long revision);

    [LoggerMessage(Level = LogLevel.Warning, Message = "未能清除版本 r{Revision} 的“运行中”标记；下次启动会把这次停止计为非正常退出。")]
    private static partial void LogMarkerNotCleared(ILogger logger, long revision);

    [LoggerMessage(Level = LogLevel.Error, Message = "停止运行时出错；仍按正常停止清除“运行中”标记。")]
    private static partial void LogRuntimeStopFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "读写配置库中的版本信息失败。")]
    private static partial void LogStorageFailure(ILogger logger);
}

/// <summary>协调者的状态，供就绪检查与状态查询使用。</summary>
internal enum RuntimeCoordinatorState
{
    /// <summary>正在做启动判定。</summary>
    Starting,

    /// <summary>运行时启动条件不满足，什么都没有启动。</summary>
    Blocked,

    /// <summary>尚无有效配置：只有发送缓存在运行，视为就绪（总设计 §10.1）。</summary>
    Idle,

    /// <summary>有效版本已激活。</summary>
    Active,

    /// <summary>运行失败：输入不启动，发送缓存照常运行，<c>/readyz</c> 返回 503（总设计 §7.4）。</summary>
    RunFailed,

    Stopping,

    Stopped,
}

internal sealed record RuntimeCoordinatorStatus(RuntimeCoordinatorState State, string? Reason, long? ActiveRevision, bool QueueOperationInProgress = false);

/// <summary>启动判定：激活某个已构建的计划、进入“运行失败”，或尚无有效配置。</summary>
internal sealed record StartupDecision(PreparedPlan? Prepared, bool ActiveIsStable, string? FailureReason)
{
    public static StartupDecision Idle { get; } = new(null, false, null);

    public static StartupDecision Activate(PreparedPlan prepared, bool activeIsStable) => new(prepared, activeIsStable, null);

    public static StartupDecision Fail(string reason) => new(null, false, reason);
}
