using System.Text.RegularExpressions;

namespace TenonEdge.Abstractions.Messages;

/// <summary>
/// 业务标识的字符规则（总设计 §6.5、§10.2）：deviceKey、inputKey、connectionKey、routeKey 等创建后不可修改的标识，
/// 会拼进 MQTT Topic 与运行单元标识，因此只允许 ASCII 字母、数字和 -、_、.，长度 1—64，首字符为字母或数字。
/// </summary>
public static partial class BusinessKeys
{
    public const int MaximumLength = 64;

    public static bool IsValid(string? value) => value is not null && Pattern().IsMatch(value);

    /// <summary>不符合规则时抛出 <see cref="ArgumentException"/>，说明中带上参数名与规则。</summary>
    public static void Validate(string? value, string parameterName)
    {
        if (!IsValid(value))
        {
            throw new ArgumentException(
                $"标识“{value}”不符合规则：只允许 ASCII 字母、数字和 -、_、.，长度 1—{MaximumLength}，首字符为字母或数字。",
                parameterName);
        }
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}
