using System.Globalization;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Host.Bootstrap;
using TenonEdge.Host.Storage.Ids;

namespace TenonEdge.Host.Storage;

/// <summary>
/// 审计记录与运行事件的保留期（总设计 §3.3、§8.4）：
/// <list type="bullet">
/// <item>审计默认保留 180 天，可调长（满足网络日志留存不少于六个月的要求），不能调短，最长 <see cref="MaximumAuditRetentionDays"/> 天。
/// 部署参数 <c>TenonEdge:Audit:RetentionDays</c> 为网页参数的最低保留期。</item>
/// <item>运行事件按最近一次发生保留 7 天，最多 1 万条，超出时先删最旧的。</item>
/// </list>
/// 清理由 <see cref="DataRetentionService"/> 在启动后与之后每小时执行，分批删除，每批一个短事务。
/// </summary>
internal sealed record DataRetentionSettings(TimeSpan AuditRetention)
{
    public const string AuditRetentionDaysKey = "TenonEdge:Audit:RetentionDays";

    public const int MinimumAuditRetentionDays = 180;

    /// <summary>审计保留期的上限（约 100 年）。更长的值没有实际意义，还会使保留期换算溢出。</summary>
    public const int MaximumAuditRetentionDays = 36_500;

    public const int MaximumRuntimeEvents = 10_000;

    public static readonly TimeSpan RuntimeEventRetention = TimeSpan.FromDays(7);

    public static DataRetentionSettings Resolve(IConfiguration configuration)
    {
        var value = configuration[AuditRetentionDaysKey];
        if (string.IsNullOrWhiteSpace(value))
        {
            return new DataRetentionSettings(TimeSpan.FromDays(MinimumAuditRetentionDays));
        }

        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var days)
            || days is < MinimumAuditRetentionDays or > MaximumAuditRetentionDays)
        {
            throw new HostStartupException(
                HostExitCodes.ConfigurationError,
                $"部署参数 {AuditRetentionDaysKey} 的值“{value}”无效：审计保留期不少于 {MinimumAuditRetentionDays} 天（网络日志留存不少于六个月），" +
                $"只能调长，最长 {MaximumAuditRetentionDays} 天。");
        }

        return new DataRetentionSettings(TimeSpan.FromDays(days));
    }
}

/// <summary>
/// 按 <see cref="DataRetentionSettings"/> 清理过期的审计记录与运行事件。边缘设备开机时时钟可能不可靠（总设计 §10.1），因此：
/// <list type="bullet">
/// <item>只有保存的时间可信的记录才按保留期删除：保存的时间（运行事件取首次发生）不早于 ID 中的逻辑时间戳减去
/// <see cref="ClockBehindTolerance"/>。写入时时钟落后的记录，保存的时间过早，而逻辑时间戳只是真实时间的下限，
/// 两者都无法说明记录有多旧，这类记录不按保留期删除；运行事件仍受 1 万条上限约束。</item>
/// </list>
/// 清理时还要求内核报告同步，且墙上时间相对启动时的单调时间推算未跳变超过一小时。
/// 不能证明时间可信时暂停按时间清理，但运行事件仍受条数上限约束。
/// </summary>
internal sealed partial class DataRetentionService(
    ConfigDatabase database,
    DataRetentionSettings settings,
    TimeProvider time,
    IClockSyncStatus clock,
    ILogger<DataRetentionService> logger,
    AuditRetentionGate auditGate, Func<TimeSpan>? readAuditRetention = null) : BackgroundService
{
    private readonly DateTimeOffset _startedAt = time.GetUtcNow();
    private readonly long _startedTimestamp = time.GetTimestamp();
    private bool _timePurgeSuspended;
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);
    internal static readonly TimeSpan DeferredAuditRetryInterval = TimeSpan.FromMinutes(1);

    /// <summary>
    /// 保存的时间比 ID 中的逻辑时间戳早这么多以上时，视为写入时时钟落后。时钟正常时两者只差读取时钟的先后与小幅校时，远小于这个值。
    /// </summary>
    public static readonly TimeSpan ClockBehindTolerance = TimeSpan.FromHours(1);

    private const int BatchSize = 1000;

    /// <summary>执行一次清理，返回删除的审计记录数与运行事件数。</summary>
    public (int AuditRecords, int RuntimeEvents) PurgeExpired()
    {
        var now = time.GetUtcNow();
        // 用时差相减而非 DateTimeOffset 加法，避免极端日期附近加上运行时长溢出。
        var divergence = (now - _startedAt) - time.GetElapsedTime(_startedTimestamp);
        var trustworthy = clock.State == ClockSyncState.Synchronized
            && divergence.Duration() <= ClockBehindTolerance;
        if (_timePurgeSuspended == trustworthy)
        {
            _timePurgeSuspended = !trustworthy;
            LogTimeProtection(logger, _timePurgeSuspended);
        }

        var eventsBefore = trustworthy ? Cutoff(now, DataRetentionSettings.RuntimeEventRetention) : DateTimeOffset.MinValue;

        int audit;
        using (var retention = auditGate.TryEnterPurge())
        {
            // Capture only after acquiring the gate, which excludes parameter
            // writes throughout all batches using this cutoff.
            var auditBefore = trustworthy && retention is not null
                ? Cutoff(now, readAuditRetention?.Invoke() ?? settings.AuditRetention) : DateTimeOffset.MinValue;
            audit = retention is null ? 0 : DeleteInBatches(
            $"""
            DELETE FROM {AuditLogRecord.TableName} WHERE id IN (
                SELECT id FROM {AuditLogRecord.TableName}
                WHERE occurred_at < @before AND occurred_at >= {TrustedTimeFloorSql}
                LIMIT {BatchSize})
            """,
            PurgeParameters(auditBefore));
        }
        var events = DeleteInBatches(
            $"""
            DELETE FROM {RuntimeEventRecord.TableName} WHERE id IN (
                SELECT id FROM {RuntimeEventRecord.TableName}
                WHERE last_occurred_at < @before AND first_occurred_at >= {TrustedTimeFloorSql}
                LIMIT {BatchSize})
            """,
            PurgeParameters(eventsBefore));
        events += DeleteInBatches(
            $"""
            DELETE FROM {RuntimeEventRecord.TableName} WHERE id IN (
                SELECT id FROM {RuntimeEventRecord.TableName}
                WHERE (SELECT COUNT(*) FROM {RuntimeEventRecord.TableName}) > {DataRetentionSettings.MaximumRuntimeEvents}
                ORDER BY id
                LIMIT MIN({BatchSize}, MAX(0, (SELECT COUNT(*) FROM {RuntimeEventRecord.TableName}) - {DataRetentionSettings.MaximumRuntimeEvents})))
            """,
            new { });
        return (audit, events);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        do
        {
            try
            {
                var (audit, events) = PurgeExpired();
                if (audit > 0 || events > 0)
                {
                    LogPurged(logger, audit, events);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
            {
                LogPurgeFailed(logger, exception.Message);
            }

            // 导出最多生成15分钟；碰撞后每分钟重试，且新导出让位给已延后的清理。
            await Task.Delay(auditGate.PurgePending ? DeferredAuditRetryInterval : Interval, time, stoppingToken);
        }
        while (!stoppingToken.IsCancellationRequested);
    }

    /// <summary>由 ID 中的逻辑时间戳换算的 Unix 毫秒数减去容差；保存的时间不早于它，才认为写入时时钟可信。</summary>
    private const string TrustedTimeFloorSql = "(id >> @timestampShift) + @epoch - @tolerance";

    private static object PurgeParameters(DateTimeOffset before) => new
    {
        before = before.ToUnixTimeMilliseconds(),
        timestampShift = SnowflakeIdGenerator.TimestampShift,
        epoch = SnowflakeIdGenerator.Epoch.ToUnixTimeMilliseconds(),
        tolerance = (long)ClockBehindTolerance.TotalMilliseconds,
    };

    /// <summary>截止时间；时钟读数过早、保留期换算会越过最小时间时取最小时间，此时没有记录会被删除。</summary>
    private static DateTimeOffset Cutoff(DateTimeOffset now, TimeSpan retention) =>
        now - DateTimeOffset.MinValue > retention ? now - retention : DateTimeOffset.MinValue;

    private int DeleteInBatches(string sql, object parameters)
    {
        var total = 0;
        int deleted;
        do
        {
            deleted = database.InTransaction(client => client.Ado.ExecuteCommand(sql, parameters));
            total += deleted;
        }
        while (deleted == BatchSize);
        return total;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "已按保留期清理 {AuditRecords} 条审计记录、{RuntimeEvents} 条运行事件。")]
    private static partial void LogPurged(ILogger logger, int auditRecords, int runtimeEvents);

    [LoggerMessage(Level = LogLevel.Warning, Message = "按保留期清理审计记录与运行事件失败，下次定时清理时重试：{Reason}")]
    private static partial void LogPurgeFailed(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "日志保留期时钟保护状态变化：暂停按时间清理 {Suspended}；运行事件条数上限仍生效。")]
    private static partial void LogTimeProtection(ILogger logger, bool suspended);
}
