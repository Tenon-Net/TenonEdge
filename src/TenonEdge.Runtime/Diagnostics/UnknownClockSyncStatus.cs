using TenonEdge.Abstractions.Contracts;

namespace TenonEdge.Runtime.Diagnostics;

/// <summary>
/// 尚未检测时钟同步状态时的实现：总是“未知”。校时检测的方式在 T1-07 核实（V-14），之后由检测结果代替；
/// 未知时由网关生成的采集时间记为 gateway，而不是 gateway-unsynced（只有确认未同步时才这样标注）。
/// </summary>
public sealed class UnknownClockSyncStatus : IClockSyncStatus
{
    public ClockSyncState State => ClockSyncState.Unknown;
}
