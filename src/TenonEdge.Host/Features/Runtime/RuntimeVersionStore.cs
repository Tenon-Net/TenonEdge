using SqlSugar;
using TenonEdge.Adapters.Http;
using TenonEdge.Adapters.Mqtt;
using TenonEdge.Host.Features.Audit;
using TenonEdge.Host.Features.Configuration;
using TenonEdge.Host.Features.Credentials;
using TenonEdge.Host.Features.Deliveries;
using TenonEdge.Host.Storage;
using TenonEdge.Runtime.Deliveries;

namespace TenonEdge.Host.Features.Runtime;

/// <summary>
/// 配置库中的运行版本元数据（总设计 §7.4、§10.2）：快照、三个指针（effective、previous、pending）、“运行中”标记与稳定、非正常退出计数。
/// 每个方法是一个单独的事务；不在事务中等待网络或做耗时计算（后端实施规范 §4）。
/// </summary>
internal sealed partial class RuntimeVersionStore(ConfigDatabase database, AuditTrail audit, TimeProvider time, MqttCredentialStore credentials, DeliveryCache deliveries, HttpCredentialStore httpCredentials)
{
    /// <summary>自动操作的操作人（总设计 §7.4：自动回滚的操作人记为“系统”）。</summary>
    public const string SystemOperator = "系统";

    /// <summary>提交前中断的版本标为“未生效”时的原因。</summary>
    public const string InterruptedReason = "进程中断";

    public RuntimeVersionPointers ReadPointers()
    {
        var state = database.Client.Queryable<SystemStateRecord>().InSingle(SystemStateRecord.SingletonId)
            ?? throw new InvalidOperationException("配置库中缺少 SystemState 行。");
        return new RuntimeVersionPointers(state.EffectiveRevision, state.PreviousRevision, state.PendingRevision, state.RunningRevision, state.SeedHash);
    }

    public RuntimeSnapshotRecord? Load(long revision) =>
        database.Client.Queryable<RuntimeSnapshotRecord>().InSingle(revision);

    /// <summary>
    /// 启动恢复（总设计 §7.4）：pendingRevision 不为空说明上次在提交前中断，把该版本标为“未生效：进程中断”并清空指针。
    /// 返回被丢弃的版本号；没有时为空。
    /// </summary>
    public long? DiscardInterruptedPending() => database.InTransaction(client =>
    {
        var state = State(client);
        if (state.PendingRevision is not { } pending)
        {
            PruneHistory(client);
            return (long?)null;
        }

        MarkNotEffective(client, pending, InterruptedReason);
        state.PendingRevision = null;
        client.Updateable(state).UpdateColumns(record => new { record.PendingRevision }).ExecuteCommand();
        PruneHistory(client);
        return pending;
    });

    /// <summary>
    /// 启动保护的“先检查、后计数”（总设计 §7.4）：上次的“运行中”标记仍在，说明进程非正常退出；
    /// 该版本从未稳定时把它的非正常退出次数加一。随后清除标记，激活时再重新写入。没有标记时返回空。
    /// </summary>
    public RunningMarkerOutcome? ConsumeRunningMarker() => database.InTransaction(client =>
    {
        var state = State(client);
        if (state.RunningRevision is not { } running)
        {
            return null;
        }

        var snapshot = client.Queryable<RuntimeSnapshotRecord>().InSingle(running);
        var counted = snapshot is { IsStable: false };
        if (counted)
        {
            snapshot!.AbnormalExitCount++;
            client.Updateable(snapshot).UpdateColumns(record => new { record.AbnormalExitCount }).ExecuteCommand();
        }

        state.RunningRevision = null;
        client.Updateable(state).UpdateColumns(record => new { record.RunningRevision }).ExecuteCommand();
        return new RunningMarkerOutcome(running, counted, snapshot?.AbnormalExitCount ?? 0);
    });

    /// <summary>
    /// 冻结（总设计 §7.4 第 1 步）：分配新版本号（大于已有版本与下限，不复用），写入快照与 pendingRevision。
    /// </summary>
    public long Freeze(RuntimePlanDocument document, string origin, string? originDetail, string createdBy)
    {
        if (origin is not (RuntimeSnapshotOrigins.Seed or RuntimeSnapshotOrigins.AutoRollback))
        { throw new ArgumentException("在线应用必须使用带草稿版本检查的冻结入口。", nameof(origin)); }
        return FreezeCore(RuntimeSnapshotDocument.FromPlan(document), origin, originDetail, createdBy, expectedDraftVersion: null);
    }

    /// <summary>在线应用的冻结入口：编译在事务外完成；确认草稿版本、分配版本、写快照和 pending 在同一事务。
    /// 冻结之后的草稿编辑不影响本快照；本方法不准备、停止或启动运行单元。</summary>
    public long FreezeDraft(RuntimePlanDocument document, string expectedDraftVersion, string createdBy, string? changeDescription = null, RuntimeApplicationExpectation? expectation = null)
    {
        if (string.IsNullOrEmpty(expectedDraftVersion) || expectedDraftVersion.Length > 64)
        { throw new ArgumentException("须提供已确认的草稿版本。", nameof(expectedDraftVersion)); }
        if (expectation is not null && document.ContentHash != expectation.PlanHash)
        { throw new DraftWriteException("configuration.reviewChanged"); }
        var draft = database.InTransaction(client => ConfigurationCheckpoint.Read(client, expectedDraftVersion));
        var snapshot = RuntimeSnapshotDocument.Create(document, draft);
        return FreezeCore(snapshot, RuntimeSnapshotOrigins.Apply, null, createdBy, expectedDraftVersion, changeDescription: changeDescription, edits: draft.Edits, expectation: expectation);
    }

    public long FreezeRollback(RuntimeSnapshotRecord target, string createdBy)
    {
        if (target.State is not (RuntimeSnapshotStates.Effective or RuntimeSnapshotStates.History) || target.ActivatedAt is null)
        { throw new InvalidOperationException("只有生效过的版本可以回滚。"); }
        _ = RuntimeSnapshotDocument.Read(target);
        return FreezeCore(new(target.SchemaVersion, target.Content, target.ContentHash), RuntimeSnapshotOrigins.AutoRollback,
            $"r{target.Revision}", createdBy, expectedDraftVersion: null);
    }

    private long FreezeCore(RuntimeSnapshotDocument document, string origin, string? originDetail, string createdBy, string? expectedDraftVersion, Action<SqlSugarClient>? validate = null, string? changeDescription = null, ConfigurationEdit[]? edits = null, RuntimeApplicationExpectation? expectation = null) =>
        database.InTransaction(client =>
        {
            var description = ConfigurationChangeDescription.Normalize(changeDescription);
            ArgumentNullException.ThrowIfNull(document);
            ArgumentException.ThrowIfNullOrWhiteSpace(createdBy);
            if (expectedDraftVersion is not null)
            { ConfigurationDraft.CheckVersion(client, expectedDraftVersion); }
            validate?.Invoke(client);
            var state = State(client);
            if (expectation is not null && state.EffectiveRevision != expectation.EffectiveRevision)
            { throw new DraftWriteException("configuration.reviewChanged"); }
            if (state.PendingRevision is { } pending)
            { throw new RuntimeVersionConflictException("runtime.applicationInProgress", pending); }
            var highest = client.Ado.GetLong("SELECT COALESCE(MAX(revision), 0) FROM runtime_snapshot");
            var revision = checked(Math.Max(state.RevisionFloor, highest) + 1);
            client.Insertable(new RuntimeSnapshotRecord
            {
                Revision = revision,
                SchemaVersion = document.SchemaVersion,
                Content = document.Content,
                ContentHash = document.ContentHash,
                Origin = origin,
                OriginDetail = originDetail,
                ChangeDescription = description,
                State = RuntimeSnapshotStates.Pending,
                CreatedAt = time.GetUtcNow().ToUnixTimeMilliseconds(),
                CreatedBy = createdBy,
            }).ExecuteCommand();
            ConfigurationEditJournal.Retain(client, edits);
            state.PendingRevision = revision;
            state.RevisionFloor = revision;
            client.Updateable(state)
                .UpdateColumns(record => new { record.PendingRevision, record.RevisionFloor })
                .ExecuteCommand();
            return revision;
        });

    /// <summary>
    /// 放弃已冻结的版本（准备阶段失败）：标为“未生效”并注明原因，版本号不复用；原有效版本不受影响。
    /// 种子快照在这里记下种子的内容哈希，同一份种子不再重复导入。
    /// </summary>
    public void Abandon(long revision, string reason, string? seedHash = null) => database.InTransaction(client =>
    {
        var state = State(client);
        var snapshot = client.Queryable<RuntimeSnapshotRecord>().InSingle(revision);
        if (state.PendingRevision != revision || snapshot?.State != RuntimeSnapshotStates.Pending)
        { throw new RuntimeVersionConflictException("runtime.pendingMismatch", revision); }
        MarkNotEffective(client, revision, reason);
        state.PendingRevision = null;

        state.SeedHash = seedHash ?? state.SeedHash;
        client.Updateable(state).UpdateColumns(record => new { record.PendingRevision, record.SeedHash }).ExecuteCommand();
        PruneHistory(client);
        return true;
    });

    /// <summary>种子导入/启动回滚的接收方检查；T3 在线应用还须在静默后复核。</summary>
    public string? TargetChangeProblem(RuntimePlanDocument next, IReadOnlyList<DeliveryMigrationIntentDraft>? migrationIntents = null)
    {
        var effective = ReadPointers().EffectiveRevision;
        if (effective is null || Load(effective.Value) is not { } snapshot)
        {
            if (migrationIntents is { Count: > 0 })
            { throw new InvalidOperationException("迁移意图需要原有效配置。"); }
            return null;
        }

        var previous = RuntimeSnapshotDocument.ReadExecutionPlan(snapshot);
        var storeId = database.Client.Queryable<SystemStateRecord>().InSingle(SystemStateRecord.SingletonId)?.ConfigStoreId;
        var allowed = ValidateMigrationIntents(previous, next, migrationIntents ?? [], storeId);
        return !TargetChangeBlocks(previous, next, CancellationToken.None).Any(block => !allowed.Contains(block.ConnectionId)) ? null
            : "存在待发或失败区记录，不能删除目标或修改接收方/Topic/路径；须先发送完、显式清理或迁移。";
    }

    private static IEnumerable<(long ConnectionId, string Kind, string Recipient, string Address, bool QueueOnly)> OutputRecipients(RuntimePlanDocument document)
    {
        foreach (var unit in document.Units)
        {
            if (unit.Id.Kind == "output.mqtt")
            {
                var settings = MqttOutputSettings.Parse(unit.Configuration);
                yield return (settings.ConnectionId, unit.Id.Kind, settings.Recipient, settings.Topic, settings.QueueOnly);
            }
            else if (unit.Id.Kind == "output.http")
            {
                var settings = HttpOutputSettings.Parse(unit.Configuration);
                yield return (settings.ConnectionId, unit.Id.Kind, settings.BaseAddress, settings.QueueOnly ? "" : settings.Endpoint.AbsoluteUri, settings.QueueOnly);
            }
        }
    }

    /// <summary>
    /// 提交（总设计 §7.4 第 4 步）：单事务中 previous = effective、effective = 新版本、清空 pending，原有效版本转为历史；
    /// 有审计记录时在同一事务中写入。种子快照在同一事务中记下种子的内容哈希。
    /// 冻结之后、提交或放弃之前进程退出时不记，下次启动丢弃中断的版本后重新导入同一份种子。
    /// 在线应用指定 markRunning，把新运行标记也放入本事务，避免提交之后仍遗留旧版本标记。
    /// 人工回滚同时条件替换草稿；并发编辑使替换跳过，不阻止有效版本提交。
    /// </summary>
    /// <returns>是否替换了草稿，不表示提交成败；普通应用或保留并发草稿时为 false，提交失败抛异常。</returns>
    public bool Commit(long revision, AuditEntry? auditEntry, string? seedHash = null, bool markRunning = false, string? replaceDraftVersion = null,
        IReadOnlyList<DeliveryMigrationIntentDraft>? migrationIntents = null, RuntimeRecoveryAuthorization? recovery = null)
    {
        var intents = migrationIntents?.ToArray() ?? [];
        var frozen = replaceDraftVersion is null ? null : Load(revision) ?? throw new KeyNotFoundException("配置版本不存在。");
        var replacement = frozen is null ? null : new ConfigurationReplacement(RequiredDraft(frozen));
        return database.InTransaction(client =>
        {
            var state = State(client);
            var snapshot = client.Queryable<RuntimeSnapshotRecord>().InSingle(revision)
                ?? throw new InvalidOperationException($"版本 r{revision} 不存在。");
            if (state.PendingRevision != revision || snapshot.State != RuntimeSnapshotStates.Pending)
            { throw new RuntimeVersionConflictException("runtime.pendingMismatch", revision); }
            if ((snapshot.Origin == RuntimeSnapshotOrigins.Recovery) != (recovery is not null))
            { throw new InvalidOperationException("损坏快照恢复须有独立管理员确认。"); }
            if (recovery is not null)
            {
                ValidateRecovery(client, recovery, requireDraftVersion: false);
                if (replaceDraftVersion is not null || intents.Length != 0 || !markRunning ||
                    auditEntry?.Action != AuditActions.SnapshotRecovered || snapshot.ContentHash != recovery.Checkpoint.Target.ContentHash ||
                    snapshot.Content != recovery.Checkpoint.Target.Content || CheckRecoveryQueue(recovery, CancellationToken.None).BlockedCount != 0)
                { throw new DraftWriteException("configuration.recoveryTargetBlocked"); }
            }
            if ((snapshot.Origin == RuntimeSnapshotOrigins.Rollback) != (replacement is not null) ||
                frozen is not null && frozen.ContentHash != snapshot.ContentHash)
            { throw new InvalidOperationException("人工回滚须使用对应的完整草稿检查点提交。"); }
            if (replacement is not null)
            { ValidateRollbackReferences(client, RequiredDraft(snapshot)); }

            if (state.EffectiveRevision is { } old && old != revision)
            {
                client.Updateable<RuntimeSnapshotRecord>()
                    .SetColumns(record => record.State == RuntimeSnapshotStates.History)
                    .Where(record => record.Revision == old)
                    .ExecuteCommand();
            }

            var checkpoint = RuntimeSnapshotDocument.ReadForExecution(snapshot);
            var document = checkpoint.Plan;
            var previous = recovery is null && state.EffectiveRevision is { } prior ? RuntimeSnapshotDocument.ReadExecutionPlan(RequiredSnapshot(client, prior)) : null;
            var allowed = ValidateMigrationIntents(previous, document, intents, state.ConfigStoreId);
            if (intents.Length > 0 && (snapshot.Origin != RuntimeSnapshotOrigins.Apply || !markRunning ||
                auditEntry?.Action != AuditActions.SnapshotApplied || auditEntry.Result != AuditResults.Success ||
                string.IsNullOrWhiteSpace(auditEntry.AccountName)))
            { throw new InvalidOperationException("迁移意图只能随有操作人和审计的在线应用提交，不能隐式用于回滚或种子。"); }
            if (previous is not null && TargetChangeBlocks(previous, document, CancellationToken.None).Any(block => !allowed.Contains(block.ConnectionId)))
            { throw new InvalidOperationException("存在未获迁移确认的接收方/Topic/路径变更或目标删除。"); }
            if (intents.Length > 0)
            { DeliveryMigrationStore.RegisterIntents(client, intents, revision, state.EffectiveRevision!.Value, auditEntry!, time.GetUtcNow().ToUnixTimeMilliseconds(), audit); }

            credentials.InvalidateChangedRecipients(client, document);
            httpCredentials.InvalidateChangedRecipients(client, document);

            snapshot.State = RuntimeSnapshotStates.Effective;
            snapshot.ActivatedAt = time.GetUtcNow().ToUnixTimeMilliseconds();
            client.Updateable(snapshot).UpdateColumns(record => new { record.State, record.ActivatedAt }).ExecuteCommand();
            if (markRunning)
            { state.RunningRevision = revision; }
            state.PreviousRevision = state.EffectiveRevision;
            state.EffectiveRevision = revision;
            if (state.PendingRevision == revision)
            {
                state.PendingRevision = null;
            }

            state.SeedHash = seedHash ?? state.SeedHash;
            client.Updateable(state)
                .UpdateColumns(record => new { record.PreviousRevision, record.EffectiveRevision, record.PendingRevision, record.SeedHash, record.RunningRevision })
                .ExecuteCommand();
            if (auditEntry is not null)
            {
                audit.Append(client, auditEntry);
            }

            var replaced = replacement?.Apply(client, replaceDraftVersion!, time) ?? false;
            if (recovery is null)
            { ConfigurationEditJournal.PruneCommitted(client, checkpoint.Draft?.Edits); }
            PruneHistory(client);
            return replaced;
        });
    }

    /// <summary>
    /// 同一来源、同一内容（自动回滚时为同一回滚目标）在提交前连续中断的次数：导入种子或自动回滚时进程反复崩溃，
    /// 每次重启都会冻结一个新版本并在下次启动时标为“未生效：进程中断”，据此设上限。
    /// 只计最近一段连续的中断：其后（按版本号）有一个生效过或因其他原因未生效（例如构建失败）的版本，之前的中断不再计入。
    /// 提交或放弃时配置库写入失败留下的 pending 同样在下次启动时标为“进程中断”，一并计入。
    /// </summary>
    public int CountInterrupted(string origin, string contentHash, string? originDetail) =>
        database.Client.Queryable<RuntimeSnapshotRecord>()
            .Where(record => record.Origin == origin && record.ContentHash == contentHash)
            .WhereIF(originDetail is not null, record => record.OriginDetail == originDetail)
            .OrderBy(record => record.Revision, OrderByType.Desc)
            .Select(record => new { record.State, record.NotEffectiveReason })
            .ToList()
            .TakeWhile(record => record.State == RuntimeSnapshotStates.NotEffective && record.NotEffectiveReason == InterruptedReason)
            .Count();

    /// <summary>记下种子的内容哈希而不导入：同一份种子不再尝试。</summary>
    public void RecordSeedHash(string seedHash) => database.InTransaction(client =>
    {
        var state = State(client);
        state.SeedHash = seedHash;
        client.Updateable(state).UpdateColumns(record => new { record.SeedHash }).ExecuteCommand();
        return true;
    });

    /// <summary>
    /// 落盘“运行中”标记（总设计 §7.4 启动保护）：加载有效版本时在构建其运行单元之前写入，构建或激活过程中崩溃都计为非正常退出；
    /// 新导入或自动回滚的版本在提交之后写入，它们在提交之前的构建由中断上限处理（<see cref="CountInterrupted"/>）。
    /// </summary>
    public void SetRunningMarker(long revision) => database.InTransaction(client =>
    {
        var state = State(client);
        if (state.EffectiveRevision != revision)
        { throw new RuntimeVersionConflictException("runtime.effectiveMismatch", revision); }
        state.RunningRevision = revision;
        client.Updateable(state).UpdateColumns(record => new { record.RunningRevision }).ExecuteCommand();
        return true;
    });

    /// <summary>按停机顺序正常退出时清除“运行中”标记；标记已指向其他版本时不改动。</summary>
    public void ClearRunningMarker(long revision) => database.InTransaction(client =>
    {
        var state = State(client);
        if (state.RunningRevision == revision)
        {
            state.RunningRevision = null;
            client.Updateable(state).UpdateColumns(record => new { record.RunningRevision }).ExecuteCommand();
        }

        return true;
    });

    /// <summary>把当前有效版本记为“已稳定”；此后它上面的退出不再计入启动保护。旧版本或已稳定时返回 false。</summary>
    public bool MarkStable(long revision) => database.InTransaction(client =>
    {
        if (State(client).EffectiveRevision != revision)
        { return false; }
        var stableAt = time.GetUtcNow().ToUnixTimeMilliseconds();
        return client.Updateable<RuntimeSnapshotRecord>()
            .SetColumns(record => new RuntimeSnapshotRecord { IsStable = true, StableAt = stableAt })
            .Where(record => record.Revision == revision && record.IsStable == false)
            .ExecuteCommand() > 0;
    });

    private static SystemStateRecord State(SqlSugarClient client) =>
        client.Queryable<SystemStateRecord>().InSingle(SystemStateRecord.SingletonId)
        ?? throw new InvalidOperationException("配置库中缺少 SystemState 行。");

    private static void MarkNotEffective(SqlSugarClient client, long revision, string reason) =>
        client.Updateable<RuntimeSnapshotRecord>()
            .SetColumns(record => new RuntimeSnapshotRecord { State = RuntimeSnapshotStates.NotEffective, NotEffectiveReason = reason })
            .Where(record => record.Revision == revision && record.State == RuntimeSnapshotStates.Pending)
            .ExecuteCommand();
}

/// <summary>SystemState 中与运行版本相关的指针。</summary>
internal sealed record RuntimeVersionPointers(
    long? EffectiveRevision,
    long? PreviousRevision,
    long? PendingRevision,
    long? RunningRevision,
    string? SeedHash);

/// <summary>处理“运行中”标记的结果：上次运行的版本、是否计入了非正常退出及计入后的次数。</summary>
internal sealed record RunningMarkerOutcome(long Revision, bool Counted, int AbnormalExitCount);

/// <summary>版本操作的并发/归属冲突；不含快照内容或凭证。</summary>
internal sealed class RuntimeVersionConflictException(string code, long revision) : InvalidOperationException(code)
{
    public string Code { get; } = code;
    public long Revision { get; } = revision;
}
