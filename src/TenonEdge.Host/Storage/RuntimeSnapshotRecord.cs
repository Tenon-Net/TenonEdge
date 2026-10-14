using SqlSugar;

namespace TenonEdge.Host.Storage;

/// <summary>
/// RuntimeSnapshot（总设计 §7.4、§10.2）：一个配置版本的完整非秘密快照及其最小元数据。
/// T1 的快照内容是种子快照规范化后的运行计划（<see cref="SchemaVersion"/> 为 1）；T3 在线应用使用版本 2，
/// 包含完整非秘密草稿和编译后的运行计划。加载时按版本解析；凭证不进入快照。时间为 UTC Unix 毫秒。
/// </summary>
[SugarTable(TableName)]
internal sealed class RuntimeSnapshotRecord
{
    public const string TableName = "runtime_snapshot";

    [SugarColumn(ColumnName = "revision", IsPrimaryKey = true)]
    public long Revision { get; set; }

    [SugarColumn(ColumnName = "schema_version")]
    public int SchemaVersion { get; set; }

    /// <summary>规范化的快照内容（UTF-8 JSON 文本）。</summary>
    [SugarColumn(ColumnName = "content")]
    public string Content { get; set; } = string.Empty;

    /// <summary>形如 sha256:十六进制小写 的内容哈希；加载时重新计算核对，不符即视为快照损坏。</summary>
    [SugarColumn(ColumnName = "content_hash")]
    public string ContentHash { get; set; } = string.Empty;

    /// <summary>版本的来源，取值见 <see cref="RuntimeSnapshotOrigins"/>。</summary>
    [SugarColumn(ColumnName = "origin")]
    public string Origin { get; set; } = string.Empty;

    /// <summary>来源的补充说明：种子快照为种子内容哈希，自动回滚为回滚到的版本号。</summary>
    [SugarColumn(ColumnName = "origin_detail", IsNullable = true)]
    public string? OriginDetail { get; set; }

    /// <summary>本次人工应用或回滚的可选说明，不属于配置内容，不随回滚复制。</summary>
    [SugarColumn(ColumnName = "change_description", IsNullable = true)]
    public string? ChangeDescription { get; set; }

    /// <summary>取值见 <see cref="RuntimeSnapshotStates"/>。</summary>
    [SugarColumn(ColumnName = "state")]
    public string State { get; set; } = string.Empty;

    /// <summary>标为“未生效”的原因。</summary>
    [SugarColumn(ColumnName = "not_effective_reason", IsNullable = true)]
    public string? NotEffectiveReason { get; set; }

    /// <summary>是否已稳定：激活后连续运行满稳定时间（总设计 §8.4：60 秒）。已稳定的版本上的退出不计入启动保护。</summary>
    [SugarColumn(ColumnName = "is_stable")]
    public bool IsStable { get; set; }

    [SugarColumn(ColumnName = "stable_at", IsNullable = true)]
    public long? StableAt { get; set; }

    /// <summary>从未稳定时累计的非正常退出次数（启动保护）。</summary>
    [SugarColumn(ColumnName = "abnormal_exit_count")]
    public int AbnormalExitCount { get; set; }

    [SugarColumn(ColumnName = "created_at")]
    public long CreatedAt { get; set; }

    /// <summary>操作人：账号，或自动操作时的“系统”。</summary>
    [SugarColumn(ColumnName = "created_by")]
    public string CreatedBy { get; set; } = string.Empty;

    /// <summary>成为有效版本（提交）的时间。</summary>
    [SugarColumn(ColumnName = "activated_at", IsNullable = true)]
    public long? ActivatedAt { get; set; }
}

/// <summary>运行快照的状态（总设计 §7.4、§10.2）：生效中、历史、未生效，以及已冻结待提交。</summary>
internal static class RuntimeSnapshotStates
{
    public const string Pending = "pending";

    public const string Effective = "effective";

    public const string History = "history";

    public const string NotEffective = "notEffective";
}

/// <summary>版本的来源；人工回滚同样生成新版本，不改写目标历史。</summary>
internal static class RuntimeSnapshotOrigins
{
    public const string Apply = "apply";

    public const string Rollback = "rollback";

    public const string Recovery = "recovery";

    /// <summary>启动时从种子快照文件导入（T1）。</summary>
    public const string Seed = "seed";

    /// <summary>启动保护自动回滚生成的版本，内容与回滚目标相同。</summary>
    public const string AutoRollback = "autoRollback";
}
