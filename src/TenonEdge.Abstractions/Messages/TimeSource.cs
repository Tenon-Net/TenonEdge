using System.Text.Json.Serialization;

namespace TenonEdge.Abstractions.Messages;

/// <summary>
/// 采集时间（collectedAt）的来源（总设计 §6.4“时间规则”）。只有映射了上游时间字段时才是 device 或 upstream；
/// 否则采集时间就是网关的接收时间，按系统时钟是否已同步记为 gateway 或 gateway-unsynced。
/// 枚举自带字符串转换器，按下面固定的名称写出。
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<TimeSource>))]
public enum TimeSource
{
    /// <summary>设备报文给出的采样时间。</summary>
    [JsonStringEnumMemberName("device")]
    Device,

    /// <summary>上游事件时间，例如 ChirpStack 的 time。</summary>
    [JsonStringEnumMemberName("upstream")]
    Upstream,

    /// <summary>网关接收时间，不代表采样时刻。</summary>
    [JsonStringEnumMemberName("gateway")]
    Gateway,

    /// <summary>系统时钟未同步时的网关时间，不可信。</summary>
    [JsonStringEnumMemberName("gateway-unsynced")]
    GatewayUnsynced,
}
