namespace TenonEdge.Adapters.Modbus;

/// <summary>设备级（连接 + UnitId）连续整周期失败与读取退避，不等同于连接重连退避。
/// 由调度器串行拥有；多个采集组须共享设备状态，不能每组创建一份离线判定。
/// 只记录实际完成的采集周期，overrun/连接退避/未准入周期不提供设备离线证据。</summary>
internal sealed class ModbusDevicePollingState
{
    private readonly TimeProvider _time;
    private readonly TimeSpan _initialDelay;
    private long? _failedAt;
    private TimeSpan _delay;

    internal ModbusDevicePollingState(TimeSpan period, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        if (period <= TimeSpan.Zero || period > TimeSpan.FromDays(1))
        { throw new ArgumentOutOfRangeException(nameof(period)); }
        _time = time;
        _initialDelay = TimeSpan.FromSeconds(Math.Min(60, Math.Max(1, period.TotalSeconds * 2)));
    }

    internal int ConsecutiveFailures { get; private set; }
    internal bool IsOffline => ConsecutiveFailures >= 3;
    internal TimeSpan RetryRemaining => _failedAt is { } at
        ? TimeSpan.FromTicks(Math.Max(0, (_delay - _time.GetElapsedTime(at)).Ticks)) : TimeSpan.Zero;
    internal bool CanRead => RetryRemaining == TimeSpan.Zero;

    internal void Record(ModbusCycleResult cycle)
    {
        ArgumentNullException.ThrowIfNull(cycle);
        if (cycle.Overrun || !cycle.HasActualAttempt)
        { return; }
        if (cycle.HasSuccessfulRead)
        {
            ConsecutiveFailures = 0;
            _failedAt = null;
            _delay = TimeSpan.Zero;
            return;
        }
        ConsecutiveFailures = Math.Min(3, ConsecutiveFailures + 1);
        if (!IsOffline)
        { return; }
        _delay = _delay == TimeSpan.Zero ? _initialDelay : TimeSpan.FromSeconds(Math.Min(60, _delay.TotalSeconds * 2));
        _failedAt = _time.GetTimestamp();
    }
}
