using System.Text;
using System.Text.Json;
using TenonEdge.Abstractions.Messages;

namespace TenonEdge.Abstractions.Contracts;

/// <summary>扩展传输的非秘密冻结路由。接收方与每条记录的相对地址分开；历史/事务读取不调用扩展代码。
/// 地址是常量或只含 {deviceKey} 的白名单模板，不执行表达式或根据消息值改换接收方。</summary>
public sealed record OutputTransportRoute(string Recipient, string AddressTemplate)
{
    public const int MaximumAddressBytes = 4096;
    private const string DeviceKey = "{deviceKey}";

    public static OutputTransportRoute? Read(JsonElement configuration, string kind, bool required = false)
    {
        if (configuration.ValueKind != JsonValueKind.Object)
        { throw Invalid(); }
        if (!configuration.TryGetProperty("transportRoute", out var value))
        { return required ? throw Invalid() : null; }
        if (kind is "output.http" or "output.mqtt" or "output.tenon" || !kind.StartsWith("output.", StringComparison.Ordinal) ||
            value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Count() != 2 ||
            !value.TryGetProperty("recipient", out var recipient) || recipient.ValueKind != JsonValueKind.String ||
            !value.TryGetProperty("addressTemplate", out var address) || address.ValueKind != JsonValueKind.String)
        { throw Invalid(); }
        var result = new OutputTransportRoute(recipient.GetString()!, address.GetString()!);
        result.Validate(kind[7..]);
        return result;
    }

    public string AddressFor(string deviceKey)
    {
        BusinessKeys.Validate(deviceKey, nameof(deviceKey));
        return AddressTemplate.Replace(DeviceKey, deviceKey, StringComparison.Ordinal);
    }

    private void Validate(string protocol)
    {
        // Reuse the same versioned identity rules: no user-info, query, fragment,
        // controls, ambiguous scheme or unspecified port in a recipient address.
        _ = OutputRecipientIdentity.Extension(protocol, Recipient, "none", null).Freeze();
        if (AddressTemplate is null || AddressTemplate.Any(char.IsControl))
        { throw Invalid(); }
        var literal = AddressTemplate.Replace(DeviceKey, "", StringComparison.Ordinal);
        var substitutions = (AddressTemplate.Length - literal.Length) / DeviceKey.Length;
        if (literal.Contains('{', StringComparison.Ordinal) || literal.Contains('}', StringComparison.Ordinal) ||
            Encoding.UTF8.GetByteCount(literal) + 64L * substitutions > MaximumAddressBytes)
        { throw Invalid(); }
    }

    private static ArgumentException Invalid() => new("扩展输出的冻结接收方或地址模板无效。");
}
