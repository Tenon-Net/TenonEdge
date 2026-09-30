namespace TenonEdge.Host.Storage;

/// <summary>
/// 审计记录与运行事件写不进配置库时改写到标准输出（总设计 §3.3：不阻断正常运行）的限额。
/// 为免在存储异常期间无限写日志，每类记录每分钟最多逐条输出 <see cref="LinesPerMinute"/> 条：
/// 额度用完时输出一行提示，其余的只计数；下一次输出前先汇总一行“另有 N 条未逐条输出”。
/// 因此每类记录每分钟最多 <see cref="LinesPerMinute"/> + 2 行。记录内容由调用方以结构化字段写出，这里只负责限额。
/// 日志的保存、轮转与大小上限由 journald 或容器日志驱动负责（总设计 §3.3），不自建文件日志。
/// </summary>
internal sealed partial class StorageWriteFallback(TimeProvider time, ILogger<StorageWriteFallback> logger)
{
    public const int LinesPerMinute = 20;

    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly Lock _lock = new();
    private readonly Dictionary<string, WindowState> _windows = new(StringComparer.Ordinal);

    /// <summary>
    /// 为一条写入失败的记录申请逐条输出的额度。返回 true 时由调用方把这条记录写到日志；
    /// 返回 false 表示本分钟的额度已用完，这一条只被计数。
    /// </summary>
    public bool TryAcquire(string kind)
    {
        var now = time.GetUtcNow();
        WindowState? previous = null;
        bool acquired;
        bool limitReached;
        lock (_lock)
        {
            if (!_windows.TryGetValue(kind, out var state) || now - state.Start >= Window || now < state.Start)
            {
                previous = state is { Suppressed: > 0 } ? state : null;
                state = new WindowState(now);
                _windows[kind] = state;
            }

            acquired = state.Written < LinesPerMinute;
            if (acquired)
            {
                state.Written++;
            }
            else
            {
                state.Suppressed++;
            }

            limitReached = state.Suppressed == 1 && !acquired;
        }

        if (previous is not null)
        {
            LogSuppressed(logger, kind, previous.Start, previous.Suppressed);
        }

        if (limitReached)
        {
            LogLimitReached(logger, kind, LinesPerMinute);
        }

        return acquired;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Kind}写入配置库失败的日志已达每分钟 {Limit} 条的上限，本分钟内其余的只计数。")]
    private static partial void LogLimitReached(ILogger logger, string kind, int limit);

    [LoggerMessage(Level = LogLevel.Warning, Message = "自 {WindowStart:O} 起的一分钟内另有 {Count} 条{Kind}写入配置库失败，未逐条输出。")]
    private static partial void LogSuppressed(ILogger logger, string kind, DateTimeOffset windowStart, int count);

    private sealed class WindowState(DateTimeOffset start)
    {
        public DateTimeOffset Start { get; } = start;

        public int Written { get; set; }

        public int Suppressed { get; set; }
    }
}
