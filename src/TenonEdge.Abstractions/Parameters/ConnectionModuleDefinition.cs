using TenonEdge.Abstractions.Plans;

namespace TenonEdge.Abstractions.Parameters;

/// <summary>输入/输出适配器随程序显式注册的辅助连接声明，不是第四类可执行扩展。
/// 声明本身不构建连接、不访问凭证或执行代码。协议语义仍由适配器纯构建校验。
/// 连接参数不含秘密；Credential 只声明受保护存储的字段，不允许进入冻结参数。</summary>
public sealed record ConnectionModuleDefinition
{
    public ConnectionModuleDefinition(string kind, int version, string displayName, ParameterSchema parameters)
    {
        if (!IsModuleKind(kind) || version < 1 || string.IsNullOrWhiteSpace(displayName) || displayName.Length > 128 ||
            displayName.Any(char.IsControl) || parameters is not { Scope: ParameterScope.Connection })
        { throw new ArgumentException("连接模块声明无效。"); }
        Kind = kind;
        Version = version;
        DisplayName = displayName;
        Parameters = parameters;
    }

    public string Kind { get; }
    public int Version { get; }
    public string DisplayName { get; }
    public ParameterSchema Parameters { get; }

    public static bool IsModuleKind(string? kind)
    {
        if (kind is null or "http" or "mqtt" or "modbus" or "tenon")
        { return false; }
        try
        { _ = RuntimeUnitId.Create(kind, "connection"); return true; }
        catch (ArgumentException) { return false; }
    }
}
