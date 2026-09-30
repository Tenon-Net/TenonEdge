using SqlSugar;

namespace TenonEdge.Host.Storage;

/// <summary>
/// 一条审计记录（总设计 §3.3、§10.2 AuditLog）：时间、用户（用令牌操作时另记令牌名称）、来源 IP、操作、对象、结果。
/// 账号以名称保存，删除用户后仍保留原账号名；恢复备份时不被覆盖。主键为雪花 ID，按时间有序，供键集分页。
/// 说明字段不得包含密码、令牌、私钥等秘密。
/// </summary>
[SugarTable(TableName)]
internal sealed class AuditLogRecord
{
    public const string TableName = "audit_log";

    [SugarColumn(ColumnName = "id", IsPrimaryKey = true)]
    public long Id { get; set; }

    /// <summary>UTC Unix 毫秒。</summary>
    [SugarColumn(ColumnName = "occurred_at")]
    public long OccurredAt { get; set; }

    [SugarColumn(ColumnName = "account_name", IsNullable = true)]
    public string? AccountName { get; set; }

    [SugarColumn(ColumnName = "token_name", IsNullable = true)]
    public string? TokenName { get; set; }

    [SugarColumn(ColumnName = "source_ip", IsNullable = true)]
    public string? SourceIp { get; set; }

    [SugarColumn(ColumnName = "action")]
    public string Action { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "target", IsNullable = true)]
    public string? Target { get; set; }

    [SugarColumn(ColumnName = "result")]
    public string Result { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "detail", IsNullable = true)]
    public string? Detail { get; set; }
}
