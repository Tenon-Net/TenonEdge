namespace TenonEdge.Host.Features.SystemInfo;

/// <summary>首次设置和后续改名共用同一非秘密显示名称规则。</summary>
internal static class GatewayNameRules
{
    public const int MaximumLength = 64;

    public static string? Normalize(string? value)
    {
        var name = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        if (name is not null && (name.Length > MaximumLength || name.Any(char.IsControl)))
        { throw new ArgumentException($"网关名称为 1—{MaximumLength} 个字符，不能包含控制字符。"); }
        return name;
    }
}
