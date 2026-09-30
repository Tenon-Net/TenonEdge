namespace TenonEdge.Host.Security;

/// <summary>
/// 密码规则（总设计 §3.3，功能需求 §1.4）：至少 8 位，同时含字母和数字，不能与账号相同，不能是内置弱口令表中的密码；
/// 不强制定期修改。长度上限 128 位（文档未规定）：限制单次哈希的输入长度。
/// </summary>
internal static class PasswordPolicy
{
    public const int MinimumLength = 8;

    public const int MaximumLength = 128;

    /// <summary>常见弱口令（不区分大小写比较）。只含字母或只含数字的密码已被前一条规则拒绝，这里只列同时含字母和数字的常见组合。</summary>
    private static readonly HashSet<string> WeakPasswords = new(
        [
            "password1", "password12", "password123", "passw0rd", "p@ssw0rd", "p@ssword1",
            "admin123", "admin1234", "admin12345", "admin888", "admin@123", "admin123456", "administrator1",
            "root1234", "root123456", "test1234", "test123456", "user1234", "guest123",
            "abc12345", "abcd1234", "abc123456", "a1234567", "a12345678", "aa123456", "qq123456",
            "qwe12345", "qwer1234", "qwerty12", "qwerty123", "1qaz2wsx", "zaq12wsx", "1q2w3e4r", "1qazxsw2",
            "iloveyou1", "welcome1", "welcome123", "changeme1", "letmein1", "12345abc", "123456abc", "123qweasd",
            "tenonedge1", "tenonedge123", "gateway1", "gateway123",
        ],
        StringComparer.OrdinalIgnoreCase);

    /// <summary>检查密码；符合规则时返回 null，否则返回面向用户的原因。</summary>
    public static string? Validate(string password, string accountName)
    {
        if (password.Length < MinimumLength)
        {
            return $"密码至少 {MinimumLength} 位。";
        }

        if (password.Length > MaximumLength)
        {
            return $"密码最多 {MaximumLength} 位。";
        }

        if (!password.Any(char.IsLetter) || !password.Any(char.IsDigit))
        {
            return "密码必须同时包含字母和数字。";
        }

        if (string.Equals(password, accountName, StringComparison.OrdinalIgnoreCase))
        {
            return "密码不能与账号相同。";
        }

        return WeakPasswords.Contains(password) ? "密码过于常见，请换一个。" : null;
    }
}
