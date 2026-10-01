using System.Text.Json.Serialization;

namespace TenonEdge.Abstractions.Messages;

/// <summary>
/// 字段为 bad 时的原因码（总设计 §6.4“质量与原因码”）。首版只产生这六种；uncertain 暂不产生，留给 OPC UA 等 G1 协议。
/// 带原因码的字段不出现在消息的值中；JSON 输入中不存在的可选字段属于“未携带”，既不在值中，也没有原因码。
/// 枚举自带字符串转换器，按下面固定的名称写出。
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<QualityCode>))]
public enum QualityCode
{
    /// <summary>读取超时。</summary>
    [JsonStringEnumMemberName("readTimeout")]
    ReadTimeout,

    /// <summary>设备返回异常，例如 Modbus 异常码。</summary>
    [JsonStringEnumMemberName("deviceException")]
    DeviceException,

    /// <summary>其他读取错误，例如连接中断、响应校验失败。</summary>
    [JsonStringEnumMemberName("readError")]
    ReadError,

    /// <summary>超出目标类型的范围。</summary>
    [JsonStringEnumMemberName("overflow")]
    Overflow,

    /// <summary>浮点值为 NaN 或无穷。</summary>
    [JsonStringEnumMemberName("nonFinite")]
    NonFinite,

    /// <summary>类型无法转换。</summary>
    [JsonStringEnumMemberName("conversionFailed")]
    ConversionFailed,
}
