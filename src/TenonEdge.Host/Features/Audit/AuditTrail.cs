using SqlSugar;
using TenonEdge.Host.Storage;
using TenonEdge.Host.Storage.Ids;

namespace TenonEdge.Host.Features.Audit;

/// <summary>
/// 审计写入（总设计 §3.3，T0-07 起，T0-09 复用同一机制）：
/// <list type="bullet">
/// <item><see cref="Append"/> 在调用方的事务中写入：首次设置、主机重置、修改密码等操作与审计记录一起提交或一起回滚。</item>
/// <item><see cref="AppendOrLog"/> 单独写入：写入失败时不阻断当前操作（例如登录），改为把同样的内容写到标准输出（总设计 §3.3），
/// 输出条数受 <see cref="StorageWriteFallback"/> 限制。</item>
/// </list>
/// 审计内容不得包含密码、令牌、私钥等秘密；调用方只传入账号、来源 IP、操作、对象与结果等字段。
/// </summary>
internal sealed partial class AuditTrail(
    ConfigDatabase database,
    SnowflakeIdGenerator ids,
    TimeProvider time,
    StorageWriteFallback fallback,
    ILogger<AuditTrail> logger)
{
    private const string FallbackKind = "审计记录";

    public void Append(ISqlSugarClient client, AuditEntry entry) => client.Insertable(ToRecord(entry)).ExecuteCommand();

    public void AppendOrLog(AuditEntry entry)
    {
        var record = ToRecord(entry);
        try
        {
            using var client = database.CreateIsolatedClient();
            client.Insertable(record).ExecuteCommand();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (!fallback.TryAcquire(FallbackKind))
            {
                return;
            }

            LogAuditFallback(
                logger,
                record.Action,
                record.Result,
                record.AccountName,
                record.SourceIp,
                record.Target,
                DateTimeOffset.FromUnixTimeMilliseconds(record.OccurredAt),
                exception.Message);
        }
    }

    private AuditLogRecord ToRecord(AuditEntry entry) => new()
    {
        Id = ids.NextId(),
        OccurredAt = time.GetUtcNow().ToUnixTimeMilliseconds(),
        AccountName = Truncate(entry.AccountName),
        SourceIp = entry.SourceIp,
        Action = entry.Action,
        Target = Truncate(entry.Target),
        Result = entry.Result,
        Detail = entry.Detail,
    };

    /// <summary>账号等字段可能来自请求（例如登录失败时提交的账号名），限制长度后再保存。</summary>
    private static string? Truncate(string? value) => value is { Length: > 64 } ? value[..64] : value;

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "审计记录写入配置库失败，改写到日志：操作 {Action}，结果 {Result}，账号 {AccountName}，来源 {SourceIp}，对象 {Target}，时间 {OccurredAt:O}；原因：{Reason}")]
    private static partial void LogAuditFallback(
        ILogger logger,
        string action,
        string result,
        string? accountName,
        string? sourceIp,
        string? target,
        DateTimeOffset occurredAt,
        string reason);
}

/// <summary>一条待写入的审计记录。</summary>
internal sealed record AuditEntry(
    string Action,
    string Result,
    string? AccountName,
    string? SourceIp,
    string? Target = null,
    string? Detail = null);

/// <summary>审计操作代码。发布后不得修改已有的值：审计查询与导出按它筛选。</summary>
internal static class AuditActions
{
    public const string SetupCompleted = "setup.complete";

    public const string Login = "auth.login";

    public const string Logout = "auth.logout";

    public const string PasswordChanged = "auth.changePassword";

    public const string AdministratorReset = "host.resetAdministrator";

    /// <summary>启动时导入种子快照，生成新的有效版本（T1-01）；操作人为“系统”。</summary>
    public const string SeedSnapshotApplied = "runtime.applySeedSnapshot";

    /// <summary>启动保护自动回滚到上一生效版本的内容（总设计 §7.4）；操作人为“系统”。</summary>
    public const string AutoRollback = "runtime.autoRollback";
}

internal static class AuditResults
{
    public const string Success = "success";

    public const string Failure = "failure";
}
