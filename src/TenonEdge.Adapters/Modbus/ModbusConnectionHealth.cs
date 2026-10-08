namespace TenonEdge.Adapters.Modbus;

internal enum ModbusRequestOutcome { Success, Timeout, DeviceException, ReadError, ConnectionError }

/// <summary>单个物理连接的最近请求事实；由该连接串行执行器访问，不是全局 UnitId 表。
/// 构造时传当前计划的启用 UnitId（多个采集组可重复），尚未请求的设备不能被推定为超时。
/// 不负责设备离线诊断或轮询退避；周期跳过、快速失败的未执行块都不能调用 Record。</summary>
internal sealed class ModbusConnectionHealth
{
    private readonly Dictionary<byte, ModbusRequestOutcome?> _latest;
    private int _timedOut;

    internal ModbusConnectionHealth(IEnumerable<byte> enabledUnits)
    {
        ArgumentNullException.ThrowIfNull(enabledUnits);
        _latest = enabledUnits.Distinct().ToDictionary(unit => unit, _ => (ModbusRequestOutcome?)null);
        if (_latest.Count == 0)
        { throw new ArgumentException("Modbus 连接至少需要一个启用从站。", nameof(enabledUnits)); }
    }

    internal bool AllUnitsTimedOut => _timedOut == _latest.Count;

    /// <returns>是否满足连接级退避重连条件。协议异常应答/CRC 等读取错误不等于无响应超时。</returns>
    internal bool Record(byte unit, ModbusRequestOutcome outcome)
    {
        if (!_latest.TryGetValue(unit, out var previous))
        { throw new ArgumentOutOfRangeException(nameof(unit), "从站不属于当前连接的启用集合。"); }
        if (!Enum.IsDefined(outcome))
        { throw new ArgumentOutOfRangeException(nameof(outcome)); }
        // 连接级错误只要求换连接，不把未请求的其他从站记为超时/离线。
        if (outcome == ModbusRequestOutcome.ConnectionError)
        { return true; }
        if (previous == ModbusRequestOutcome.Timeout)
        { _timedOut--; }
        _latest[unit] = outcome;
        if (outcome == ModbusRequestOutcome.Timeout)
        { _timedOut++; }
        return AllUnitsTimedOut;
    }

    internal void ConnectionReplaced()
    {
        foreach (var unit in _latest.Keys)
        { _latest[unit] = null; }
        _timedOut = 0;
    }
}
