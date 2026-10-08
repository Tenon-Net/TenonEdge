using SqlSugar;

namespace TenonEdge.Host.Storage;

/// <summary>
/// SystemState（总设计 §10.2）：config.db 中只有一行的系统状态。各任务按需追加字段，字段含义见对应的迁移。
/// </summary>
[SugarTable(TableName)]
internal sealed class SystemStateRecord
{
    public const string TableName = "system_state";

    public const int SingletonId = 1;

    [SugarColumn(ColumnName = "id", IsPrimaryKey = true)]
    public int Id { get; set; } = SingletonId;

    /// <summary>网关首次初始化时生成的本地身份，重启保持不变（总设计 §9.3）。</summary>
    [SugarColumn(ColumnName = "node_id", IsNullable = true)]
    public string? NodeId { get; set; }

    /// <summary>配置库首次创建时生成的随机标识，随备份恢复（总设计 §10.4）。</summary>
    [SugarColumn(ColumnName = "config_store_id", IsNullable = true)]
    public string? ConfigStoreId { get; set; }

    /// <summary>内部 ID 的下限：新 ID 必须大于它（恢复备份后写入，总设计 §10.4）。</summary>
    [SugarColumn(ColumnName = "id_floor")]
    public long IdFloor { get; set; }

    /// <summary>配置版本号的下限：新版本号必须大于它（总设计 §7.3、§10.4）。</summary>
    [SugarColumn(ColumnName = "revision_floor")]
    public long RevisionFloor { get; set; }

    /// <summary>首次设置状态，取值见 <see cref="SetupStates"/>（总设计 §10.3）。</summary>
    [SugarColumn(ColumnName = "setup_state")]
    public string SetupState { get; set; } = SetupStates.Uninitialized;

    /// <summary>“重置待设置”时，等待设置新密码的管理员账号。</summary>
    [SugarColumn(ColumnName = "setup_reset_account_id", IsNullable = true)]
    public long? SetupResetAccountId { get; set; }

    /// <summary>最近一次完成设置的时间（UTC Unix 毫秒），首页与系统信息显示。</summary>
    [SugarColumn(ColumnName = "setup_completed_at", IsNullable = true)]
    public long? SetupCompletedAt { get; set; }

    /// <summary>最近一次完成设置的来源 IP。</summary>
    [SugarColumn(ColumnName = "setup_source_ip", IsNullable = true)]
    public string? SetupSourceIp { get; set; }

    [SugarColumn(ColumnName = "gateway_name", IsNullable = true)]
    public string? GatewayName { get; set; }

    /// <summary>会话代次：开启“只允许 HTTPS”或恢复备份时加一，此前签发的全部会话失效（总设计 §3.3）。</summary>
    [SugarColumn(ColumnName = "session_generation")]
    public long SessionGeneration { get; set; } = 1;

    /// <summary>当前生效的配置版本（总设计 §7.4 的 effectiveRevision）；尚无有效配置时为空。</summary>
    [SugarColumn(ColumnName = "effective_revision", IsNullable = true)]
    public long? EffectiveRevision { get; set; }

    /// <summary>上一个生效的版本（previousRevision），启动保护的自动回滚以它为目标。</summary>
    [SugarColumn(ColumnName = "previous_revision", IsNullable = true)]
    public long? PreviousRevision { get; set; }

    /// <summary>已冻结、尚未提交的版本（pendingRevision）；启动时仍不为空，说明上次在提交前中断。</summary>
    [SugarColumn(ColumnName = "pending_revision", IsNullable = true)]
    public long? PendingRevision { get; set; }

    /// <summary>“运行中”标记：已激活、尚未按停机顺序正常退出的版本（启动保护，总设计 §7.4）。</summary>
    [SugarColumn(ColumnName = "running_revision", IsNullable = true)]
    public long? RunningRevision { get; set; }

    /// <summary>最近一次导入的种子快照的内容哈希（T1 的种子快照）。</summary>
    [SugarColumn(ColumnName = "seed_hash", IsNullable = true)]
    public string? SeedHash { get; set; }
}

/// <summary>首次设置状态的取值（总设计 §10.3）。</summary>
internal static class SetupStates
{
    public const string Uninitialized = "uninitialized";

    public const string Initialized = "initialized";

    public const string ResetPending = "resetPending";
}
