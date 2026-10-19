using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using TenonEdge.Abstractions.Plans;

namespace TenonEdge.Abstractions.Parameters;

/// <summary>输入/输出适配器随程序显式注册的辅助连接声明，不是第四类可执行扩展。
/// 声明本身不构建连接、不访问凭证或执行代码。协议语义仍由适配器纯构建校验。
/// 连接参数不含秘密；Credential 只声明受保护存储的字段，不允许进入冻结参数。</summary>
public sealed record ConnectionModuleDefinition
{
    public ConnectionModuleDefinition(string kind, int version, string displayName, ParameterSchema parameters,
        IEnumerable<string>? recipientParameters = null)
    {
        if (!IsModuleKind(kind) || version < 1 || string.IsNullOrWhiteSpace(displayName) || displayName.Length > 128 ||
            displayName.Any(char.IsControl) || parameters is not { Scope: ParameterScope.Connection })
        { throw new ArgumentException("连接模块声明无效。"); }
        Kind = kind;
        Version = version;
        DisplayName = displayName;
        Parameters = parameters;
        var recipient = (recipientParameters ?? []).Take(ParameterSchema.MaximumParameters + 1).ToImmutableArray();
        if (recipient.Length > ParameterSchema.MaximumParameters || recipient.Distinct(StringComparer.Ordinal).Count() != recipient.Length ||
            recipient.Any(name => !parameters.Parameters.Any(field => field.Name == name && field.Required &&
                field.Type is not (ParameterType.Credential or ParameterType.ConnectionReference))))
        { throw new ArgumentException("接收方字段须为不重复的已声明必填非秘密标量。"); }
        RecipientParameters = recipient;
    }

    public string Kind { get; }
    public int Version { get; }
    public string DisplayName { get; }
    public ParameterSchema Parameters { get; }
    /// <summary>明确决定接收方的参数名，不包括超时等运行参数；为空时未声明通用凭证绑定能力。
    /// 不猜测host/url字段，不把全部共享配置身份误当成接收方。</summary>
    public ImmutableArray<string> RecipientParameters { get; }

    public string RecipientIdentity(JsonElement parameters)
    {
        if (RecipientParameters.IsEmpty || parameters.ValueKind != JsonValueKind.Object)
        { throw new ArgumentException("连接未声明接收方绑定。"); }
        var selected = RecipientParameters.ToDictionary(name => name,
            name => parameters.TryGetProperty(name, out var value) && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Object or JsonValueKind.Array)
                ? value : throw new ArgumentException("接收方参数不完整。"), StringComparer.Ordinal);
        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(CanonicalJson.Serialize(
            JsonSerializer.SerializeToElement(new { Kind, Version, Parameters = selected }))));
    }

    public static bool IsModuleKind(string? kind)
    {
        if (kind is null or "http" or "mqtt" or "modbus" or "tenon")
        { return false; }
        try
        { _ = RuntimeUnitId.Create(kind, "connection"); return true; }
        catch (ArgumentException) { return false; }
    }
}
