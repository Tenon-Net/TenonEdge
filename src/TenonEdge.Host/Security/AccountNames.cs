using System.Text.RegularExpressions;

namespace TenonEdge.Host.Security;

/// <summary>
/// 账号名规则（功能需求 §1.3、§9.1）：字母、数字和 -、_、.，3—32 位，创建后不可修改。
/// 唯一性与登录匹配按规范化（小写）后的账号名进行，Admin 与 admin 视为同一账号。
/// </summary>
internal static partial class AccountNames
{
    public const int MinimumLength = 3;

    public const int MaximumLength = 32;

    /// <summary>首次设置时默认填写、主机侧重置默认处理的管理员账号。</summary>
    public const string DefaultAdministrator = "admin";

    public static bool IsValid(string? accountName) => accountName is not null && Pattern().IsMatch(accountName);

    public static string Normalize(string accountName) => accountName.ToLowerInvariant();

    [GeneratedRegex("^[A-Za-z0-9._-]{3,32}$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}
