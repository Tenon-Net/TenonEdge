using SqlSugar;
using TenonEdge.Host.Features.Configuration;
using TenonEdge.Host.Storage;

namespace TenonEdge.Host.Features.Runtime;

internal sealed record RuntimeVersionSummary(long Revision, int SchemaVersion, string State, string Origin, string? OriginDetail,
    string CreatedBy, long CreatedAt, long? ActivatedAt, bool IsStable, string? NotEffectiveReason);

internal sealed partial class RuntimeVersionStore
{
    private const int RetainedEffectiveVersions = 20;
    private const int RetainedNotEffectiveVersions = 20;

    /// <summary>仅取元数据，不把每个 16 MiB 快照带入历史列表；按版本号游标分页。</summary>
    public RuntimeVersionSummary[] ReadHistory(long? beforeRevision = null, int limit = 20)
    {
        if (beforeRevision is <= 0)
        { throw new ArgumentOutOfRangeException(nameof(beforeRevision), "游标须为正版本号。"); }
        if (limit is < 1 or > 100)
        { throw new ArgumentOutOfRangeException(nameof(limit), "历史页大小为 1—100。"); }
        return database.Client.Queryable<RuntimeSnapshotRecord>()
            .WhereIF(beforeRevision is not null, row => row.Revision < beforeRevision)
            .OrderBy(row => row.Revision, OrderByType.Desc).Take(limit)
            .Select(row => new
            {
                row.Revision,
                row.SchemaVersion,
                row.State,
                row.Origin,
                row.OriginDetail,
                row.CreatedBy,
                row.CreatedAt,
                row.ActivatedAt,
                row.IsStable,
                row.NotEffectiveReason
            })
            .ToList().Select(row => new RuntimeVersionSummary(row.Revision, row.SchemaVersion, row.State, row.Origin, row.OriginDetail,
                row.CreatedBy, row.CreatedAt, row.ActivatedAt, row.IsStable, row.NotEffectiveReason)).ToArray();
    }

    public ConfigurationChange[] CompareVersions(long beforeRevision, long afterRevision)
    {
        var pair = database.InTransaction(client => (Before: RequiredSnapshot(client, beforeRevision), After: RequiredSnapshot(client, afterRevision)));
        return ConfigurationDiff.Between(RequiredDraft(pair.Before), RequiredDraft(pair.After));
    }

    /// <summary>没有基准版本时与空草稿比较。读取草稿和版本处于同一事务，比较在事务外。</summary>
    public ConfigurationChange[] CompareDraft(long? revision, string expectedDraftVersion)
    {
        var pair = database.InTransaction(client => (Before: revision is { } value ? RequiredSnapshot(client, value) : null,
            After: ConfigurationCheckpoint.Read(client, expectedDraftVersion)));
        var before = pair.Before is null ? new ConfigurationCheckpoint("0", [], [], [], []) : RequiredDraft(pair.Before);
        return ConfigurationDiff.Between(before, pair.After);
    }

    private static RuntimeSnapshotRecord RequiredSnapshot(SqlSugarClient client, long revision) =>
        client.Queryable<RuntimeSnapshotRecord>().InSingle(revision) ?? throw new KeyNotFoundException("配置版本不存在或已清理。");

    private static ConfigurationCheckpoint RequiredDraft(RuntimeSnapshotRecord snapshot) =>
        RuntimeSnapshotDocument.Read(snapshot).Draft ?? throw new NotSupportedException("旧版运行计划没有完整草稿，不能按配置对象比较。");

    // 在提交/放弃的同一事务内清理，不依赖后台任务；不复用版本号，也不删除队列/凭证/审计。
    private static void PruneHistory(SqlSugarClient client)
    {
        client.Ado.ExecuteCommand("""
            DELETE FROM runtime_snapshot
            WHERE state = 'history' AND revision NOT IN (
                SELECT revision FROM runtime_snapshot WHERE state IN ('effective', 'history') ORDER BY revision DESC LIMIT @effectiveLimit
            ) AND revision NOT IN (
                SELECT effective_revision FROM system_state WHERE effective_revision IS NOT NULL
                UNION SELECT previous_revision FROM system_state WHERE previous_revision IS NOT NULL
                UNION SELECT pending_revision FROM system_state WHERE pending_revision IS NOT NULL
                UNION SELECT running_revision FROM system_state WHERE running_revision IS NOT NULL
            )
            """, new SugarParameter("@effectiveLimit", RetainedEffectiveVersions));
        client.Ado.ExecuteCommand("""
            DELETE FROM runtime_snapshot
            WHERE state = 'notEffective' AND revision NOT IN (
                SELECT revision FROM runtime_snapshot WHERE state = 'notEffective' ORDER BY revision DESC LIMIT @failedLimit
            ) AND revision NOT IN (
                SELECT pending_revision FROM system_state WHERE pending_revision IS NOT NULL
            )
            """, new SugarParameter("@failedLimit", RetainedNotEffectiveVersions));
    }
}
