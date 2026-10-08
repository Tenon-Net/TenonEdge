using SqlSugar;
using TenonEdge.Host.Storage;
using TenonEdge.Host.Storage.Ids;

namespace TenonEdge.Host.Features.Runtime;

/// <summary>
/// 运行事件的写入（总设计 §3.3，功能需求 §7.4；T0-09 建立供 T1 起各模块使用的最小存储）：
/// <list type="bullet">
/// <item>同一对象的同类、同级别事件，自第一次发生起 <see cref="MergeWindow"/> 内合并为一条并计数（例如“5 分钟内断开 12 次”），不刷屏；
/// 说明取最近一次。超过窗口后另起一条。</item>
/// <item>写入失败时不影响正常运行，改写到标准输出，输出条数受 <see cref="StorageWriteFallback"/> 限制。</item>
/// </list>
/// 说明中不得包含报文正文、点位的值和秘密（总设计 §3.3）。各类事件的生产者随对应模块落地；T0 只有服务启停、首次设置窗口与运行时启动条件。
/// </summary>
internal sealed partial class RuntimeEventLog(
    ConfigDatabase database,
    SnowflakeIdGenerator ids,
    TimeProvider time,
    StorageWriteFallback fallback,
    ILogger<RuntimeEventLog> logger)
{
    public static readonly TimeSpan MergeWindow = TimeSpan.FromMinutes(5);

    private const string FallbackKind = "运行事件";

    public void Record(RuntimeEventLevel level, string type, string? target, string description)
    {
        var occurredAt = time.GetUtcNow();
        var now = occurredAt.ToUnixTimeMilliseconds();
        var normalizedTarget = target ?? string.Empty;
        var levelName = RuntimeEventLevels.ToName(level);
        try
        {
            database.InTransaction(client =>
            {
                var mergeFrom = now - (long)MergeWindow.TotalMilliseconds;
                var latest = client.Queryable<RuntimeEventRecord>()
                    .Where(record => record.Type == type
                        && record.Target == normalizedTarget
                        && record.Level == levelName
                        && record.FirstOccurredAt >= mergeFrom)
                    .OrderBy(record => record.Id, OrderByType.Desc)
                    .First();
                if (latest is not null)
                {
                    latest.OccurrenceCount++;
                    latest.LastOccurredAt = Math.Max(latest.LastOccurredAt, now);
                    latest.Message = description;
                    client.Updateable(latest)
                        .UpdateColumns(record => new { record.OccurrenceCount, record.LastOccurredAt, record.Message })
                        .ExecuteCommand();
                }
                else
                {
                    client.Insertable(new RuntimeEventRecord
                    {
                        Id = ids.NextId(),
                        FirstOccurredAt = now,
                        LastOccurredAt = now,
                        Level = levelName,
                        Type = type,
                        Target = normalizedTarget,
                        Message = description,
                    }).ExecuteCommand();
                }

                return true;
            });
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (fallback.TryAcquire(FallbackKind))
            {
                LogRuntimeEventFallback(logger, levelName, type, normalizedTarget, description, occurredAt, exception.Message);
            }
        }
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "运行事件写入配置库失败，改写到日志：级别 {Level}，类型 {Type}，对象 {Target}，说明 {Description}，时间 {OccurredAt:O}；原因：{Reason}")]
    private static partial void LogRuntimeEventFallback(
        ILogger logger,
        string level,
        string type,
        string target,
        string description,
        DateTimeOffset occurredAt,
        string reason);
}

/// <summary>运行事件级别，按字符串保存与序列化。</summary>
internal enum RuntimeEventLevel
{
    Info,
    Warning,
    Error,
}

/// <summary>运行事件级别与库中保存的字符串之间的转换；保存的值与 API 中的枚举字符串一致（小驼峰）。</summary>
internal static class RuntimeEventLevels
{
    public static string ToName(RuntimeEventLevel level) => level switch
    {
        RuntimeEventLevel.Info => "info",
        RuntimeEventLevel.Warning => "warning",
        RuntimeEventLevel.Error => "error",
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, "未知的运行事件级别。"),
    };

    public static bool IsName(string name) => name is "info" or "warning" or "error";

    public static RuntimeEventLevel FromName(string name) => name switch
    {
        "info" => RuntimeEventLevel.Info,
        "warning" => RuntimeEventLevel.Warning,
        "error" => RuntimeEventLevel.Error,
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "未知的运行事件级别。"),
    };
}

/// <summary>运行事件类型代码。发布后不得修改已有的值：查询按它筛选。</summary>
internal static class RuntimeEventTypes
{
    public const string ServiceStarted = "service.started";

    public const string ServiceStopping = "service.stopping";

    public const string SetupWindowOpen = "setup.windowOpen";

    public const string RuntimeBlocked = "runtime.blocked";

    // T1-01：运行版本与启动保护（总设计 §7.4）。运行单元、设备与发送缓存的事件类型见 TenonEdge.Runtime.Diagnostics.RuntimeEventTypes。

    /// <summary>种子快照已导入并成为有效版本。</summary>
    public const string SeedApplied = "runtime.seedApplied";

    /// <summary>种子快照无法读取或内容无效，没有生成版本。</summary>
    public const string SeedRejected = "runtime.seedRejected";

    /// <summary>版本在提交前被放弃，标为“未生效”（准备阶段失败或进程中断）。</summary>
    public const string VersionNotEffective = "runtime.versionNotEffective";

    /// <summary>上次运行有效版本时进程非正常退出（计入或不计入启动保护）。</summary>
    public const string AbnormalExit = "runtime.abnormalExit";

    /// <summary>有效版本连续运行满稳定时间，记为“已稳定”。</summary>
    public const string VersionStable = "runtime.versionStable";

    /// <summary>启动保护已自动回滚到上一生效版本的内容。</summary>
    public const string AutoRollback = "runtime.autoRollback";

    /// <summary>进入“运行失败”：输入不启动，发送缓存照常运行。</summary>
    public const string RunFailed = "runtime.runFailed";
}
