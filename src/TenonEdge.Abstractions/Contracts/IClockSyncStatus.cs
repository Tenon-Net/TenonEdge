using System.Text.Json.Serialization;

namespace TenonEdge.Abstractions.Contracts;

/// <summary>
/// 系统时钟的同步状态（总设计 §8.1，V-14）。检测方式在 T1-07 核实；尚未检测或无法判断时为 <see cref="ClockSyncState.Unknown"/>。
/// </summary>
public interface IClockSyncStatus
{
    ClockSyncState State { get; }
}

/// <summary>时钟同步状态，诊断页显示为已同步、未同步或未知（V-14）。</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ClockSyncState>))]
public enum ClockSyncState
{
    [JsonStringEnumMemberName("unknown")]
    Unknown,

    [JsonStringEnumMemberName("synchronized")]
    Synchronized,

    /// <summary>确认未同步：此时由网关生成的采集时间，其时间来源记为 gateway-unsynced（总设计 §6.4）。</summary>
    [JsonStringEnumMemberName("notSynchronized")]
    NotSynchronized,
}
