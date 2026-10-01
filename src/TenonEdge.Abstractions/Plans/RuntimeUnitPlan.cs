using System.Security.Cryptography;
using System.Text.Json;

namespace TenonEdge.Abstractions.Plans;

/// <summary>
/// 运行计划中的一个运行单元：确定性标识、配置与配置哈希（总设计 §7.4、§12 T0）。
/// 配置是 JSON 对象，按规范化形式保存，与调用方的文档无关，不可变，可以跨线程读取；
/// 配置哈希为规范化文本的 SHA-256：与属性顺序、空白和字符串的转义写法无关，内容变化则哈希变化；
/// 数值保留原文，因此 1、1.0 与 1E0 视为不同的配置（规范化规则见 <see cref="CanonicalJson"/>）。
/// 配置不是有效的 JSON 对象（重名属性、无效的 UTF-16 字符、嵌套超过 64 层）时抛出 <see cref="ArgumentException"/>。
/// </summary>
public sealed class RuntimeUnitPlan
{
    public RuntimeUnitPlan(RuntimeUnitId id, JsonElement configuration)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (configuration.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("运行单元的配置须为 JSON 对象。", nameof(configuration));
        }

        byte[] canonical;
        JsonElement normalized;
        try
        {
            canonical = CanonicalJson.Serialize(configuration);
            using var document = JsonDocument.Parse(canonical);
            normalized = document.RootElement.Clone();
        }
        catch (Exception exception) when (exception is InvalidOperationException or JsonException)
        {
            throw new ArgumentException($"运行单元 {id} 的配置不是有效的 JSON 对象：{exception.Message}", nameof(configuration), exception);
        }

        Id = id;
        Configuration = normalized;
        ConfigurationHash = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(canonical));
    }

    public RuntimeUnitId Id { get; }

    /// <summary>规范化后的配置。只有构建运行单元的工厂解析它，解析是纯本地计算。</summary>
    public JsonElement Configuration { get; }

    /// <summary>形如 sha256:十六进制小写 的配置哈希。</summary>
    public string ConfigurationHash { get; }
}
