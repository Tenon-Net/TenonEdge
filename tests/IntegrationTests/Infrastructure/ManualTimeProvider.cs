namespace TenonEdge.IntegrationTests.Infrastructure;

/// <summary>
/// 由测试控制的时钟：只在调用 <see cref="Advance"/> 或 <see cref="SetUtcNow"/> 时变化。
/// 单调计时（<see cref="GetTimestamp"/>）随同一时钟前进，<see cref="SetUtcNow"/> 设到更早的时间时也随之后退，
/// 需要模拟“系统时钟被校正而单调计时不受影响”时，使用 <see cref="StepWallClock"/>。
/// </summary>
public sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private readonly Lock _lock = new();
    private DateTimeOffset _utcNow = start;
    private TimeSpan _wallClockOffset;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp()
    {
        lock (_lock)
        {
            return (_utcNow - _wallClockOffset).UtcTicks;
        }
    }

    /// <summary>只移动系统时钟（例如开机后校时），单调计时不变。</summary>
    public void StepWallClock(TimeSpan delta)
    {
        lock (_lock)
        {
            _utcNow += delta;
            _wallClockOffset += delta;
        }
    }

    public override DateTimeOffset GetUtcNow()
    {
        lock (_lock)
        {
            return _utcNow;
        }
    }

    public void Advance(TimeSpan delta)
    {
        lock (_lock)
        {
            _utcNow += delta;
        }
    }

    public void SetUtcNow(DateTimeOffset value)
    {
        lock (_lock)
        {
            _utcNow = value;
        }
    }
}
