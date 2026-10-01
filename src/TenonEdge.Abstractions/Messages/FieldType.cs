using System.Text.Json.Serialization;

namespace TenonEdge.Abstractions.Messages;

/// <summary>
/// 输出字段的类型（总设计 §6.4“数值与类型”）。只有四种，由配置确定：点位类型给出默认值，映射规则的目标类型可以改写。
/// 同一字段始终使用同一种 JSON 形式，不随数值大小在数字和字符串之间切换。
/// 枚举自带字符串转换器：不配置转换器时也按下面固定的名称写出，而不是写成整数。
/// </summary>
// 成员名与总设计规定的四种输出类型一一对应（也是写出的名称），不是在描述 C# 类型，因此局部关闭 CA1720。
#pragma warning disable CA1720
[JsonConverter(typeof(JsonStringEnumConverter<FieldType>))]
public enum FieldType
{
    /// <summary>JSON 布尔值。</summary>
    [JsonStringEnumMemberName("boolean")]
    Boolean,

    /// <summary>十进制整数字面量，逐位精确，不经过 double。</summary>
    [JsonStringEnumMemberName("integer")]
    Integer,

    /// <summary>数字字面量：decimal 逐位写出、不用指数；浮点数按最短往返格式写出。</summary>
    [JsonStringEnumMemberName("number")]
    Number,

    /// <summary>JSON 字符串。</summary>
    [JsonStringEnumMemberName("string")]
    String,
}
#pragma warning restore CA1720
