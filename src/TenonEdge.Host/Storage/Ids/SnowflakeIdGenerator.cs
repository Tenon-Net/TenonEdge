// 来源：TenonAdmin（https://github.com/Tenon-Net/TenonAdmin），提交 e6473d4107a2f402fce4c05337559167be8f8a63，
// 文件 backend/src/TenonAdmin.Core/Ids/SnowflakeIdGenerator.cs，Apache License 2.0。
// TenonEdge 按总设计 §10.1 修改了此文件（2026-09-30），修改说明见 docs/reference-notes.md §3：
// 机器号固定为 0；启动时以库中最大 ID、SystemState 下限和构建时间下限确定起点；时钟落后时沿用逻辑时间戳，
// 序列用尽时逻辑时间戳加 1 毫秒而不等待时钟；时钟读数达到 2^41 毫秒时视为不可信并告警；
// 只有逻辑时间戳本身达到 2^41 毫秒时才拒绝发号；公开 TimestampShift，供按保留期清理时由 ID 换算时间。保留原实现的位布局与纪元。

namespace TenonEdge.Host.Storage.Ids;

/// <summary>
/// config.db 实体主键的 64 位雪花 ID（总设计 §10.1）。
/// <code>
/// ┌─1 bit─┬────────41 bit────────┬───6 bit───┬───6 bit───┐
/// │ 符号 0 │ 相对纪元的毫秒时间戳    │  机器号    │  毫秒内序列  │
/// └───────┴──────────────────────┴───────────┴───────────┘
/// </code>
/// <list type="bullet">
/// <item>低位固定 12 位，ID 恒小于 2^53，前端按数字解析不丢精度；不得加宽低位，否则须改为字符串序列化。</item>
/// <item>机器号固定。唯一性依赖数据目录的单实例锁：同一配置库只有一个进程发号，主机侧维护命令同样持有该锁。
/// 雪花 ID 只在一个配置库内唯一，不作为跨网关的身份。</item>
/// <item>一个进程只能有一个实例：两个实例各自计算序列，同一毫秒会发出相同的 ID。</item>
/// <item>边缘设备开机时时钟可能不可靠，因此时间戳是“逻辑时间戳”：只增不减，时钟落后时沿用，从不等待时钟。</item>
/// </list>
/// </summary>
internal sealed partial class SnowflakeIdGenerator
{
    /// <summary>机器号固定为 0。</summary>
    public const long WorkerId = 0;

    /// <summary>41 位毫秒时间戳的上限（不含）。</summary>
    public const long TimestampLimit = 1L << 41;

    /// <summary>ID 的上限（不含）：2^53。</summary>
    public const long IdLimit = 1L << 53;

    private const int WorkerIdBits = 6;
    private const int SequenceBits = 6;
    private const long SequenceMask = (1L << SequenceBits) - 1;
    private const int WorkerIdShift = SequenceBits;
    /// <summary>时间戳在 ID 中的起始位：低 12 位为机器号与序列。</summary>
    public const int TimestampShift = SequenceBits + WorkerIdBits;
    private const long LowBitsMask = (1L << TimestampShift) - 1;

    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Lock _lock = new();

    private long _lastTimestamp;
    private long _sequence;
    private bool _untrustedClockReported;

    /// <param name="floor">新 ID 必须大于的值：config.db 中已有的最大 ID 与 SystemState 下限中的较大者；没有时为 0。</param>
    /// <param name="buildTimestampFloor">本版本的构建时间下限：新 ID 的时间戳不早于它。</param>
    /// <param name="time">时间源；测试注入可控时钟。</param>
    /// <param name="logger">时钟读数不可信时的告警。</param>
    public SnowflakeIdGenerator(long floor, DateTimeOffset buildTimestampFloor, TimeProvider time, ILogger logger)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(floor);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(floor, IdLimit);
        _time = time;
        _logger = logger;

        var buildTimestamp = Math.Clamp(MillisecondsSinceEpoch(buildTimestampFloor), 0, TimestampLimit - 1);
        var floorTimestamp = floor >> TimestampShift;
        if (floor > 0 && floorTimestamp >= buildTimestamp)
        {
            // 下一个 ID 从下限所在的毫秒继续：同一毫秒内只有序列更大的 ID 才大于下限；
            // 下限的机器号位与本机不同时（例如来自其他来源的数据），直接视为该毫秒已用尽。
            _lastTimestamp = floorTimestamp;
            _sequence = ((floor & LowBitsMask) >> WorkerIdShift) == WorkerId ? floor & SequenceMask : SequenceMask;
        }
        else
        {
            _lastTimestamp = buildTimestamp;
            _sequence = -1;
        }
    }

    /// <summary>纪元：2020-02-20 02:20:02 UTC，沿用原实现（与 Yitter.IdGenerator 的默认纪元一致）。只能往更早挪。</summary>
    public static DateTimeOffset Epoch { get; } = new(2020, 2, 20, 2, 20, 2, TimeSpan.Zero);

    public long NextId()
    {
        lock (_lock)
        {
            long timestamp;
            long sequence;
            if (ReadClock() is { } clock && clock > _lastTimestamp)
            {
                timestamp = clock;
                sequence = 0;
            }
            else
            {
                // 时钟落后、停滞或不可信：沿用逻辑时间戳；本毫秒的序列用尽时逻辑时间戳加 1，不等待时钟。
                timestamp = _lastTimestamp;
                sequence = _sequence + 1;
                if (sequence > SequenceMask)
                {
                    timestamp++;
                    sequence = 0;
                }
            }

            if (timestamp >= TimestampLimit)
            {
                throw new InvalidOperationException("雪花 ID 的逻辑时间戳已达到 41 位上限（2^41 毫秒），拒绝发号。");
            }

            _lastTimestamp = timestamp;
            _sequence = sequence;
            return (timestamp << TimestampShift) | (WorkerId << WorkerIdShift) | sequence;
        }
    }

    /// <summary>从 ID 取出相对纪元的毫秒时间戳。</summary>
    public static long TimestampOf(long id) => id >> TimestampShift;

    private static long MillisecondsSinceEpoch(DateTimeOffset value) =>
        (value - Epoch).Ticks / TimeSpan.TicksPerMillisecond;

    /// <summary>读取时钟；读数达到 2^41 毫秒时视为不可信，返回 null 并告警（在时钟恢复可信之前只告警一次）。</summary>
    private long? ReadClock()
    {
        var now = _time.GetUtcNow();
        var milliseconds = MillisecondsSinceEpoch(now);
        if (milliseconds < TimestampLimit)
        {
            _untrustedClockReported = false;
            return milliseconds;
        }

        if (!_untrustedClockReported)
        {
            _untrustedClockReported = true;
            LogUntrustedClock(_logger, now);
        }

        return null;
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "系统时钟读数 {Now:O} 超出雪花 ID 的 41 位时间范围，视为不可信；继续使用逻辑时间戳发号。请检查时钟同步。")]
    private static partial void LogUntrustedClock(ILogger logger, DateTimeOffset now);
}
