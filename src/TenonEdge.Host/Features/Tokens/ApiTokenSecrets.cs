using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace TenonEdge.Host.Features.Tokens;

/// <summary>256位随机秘密，不是JWT。公开编号只用于查找；哈希绑定完整版本/编号/秘密，不接受查询串或Cookie。</summary>
internal static class ApiTokenSecrets
{
    public static string Create(long id) => $"te1.{id.ToString(CultureInfo.InvariantCulture)}.{Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_')}";
    public static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));
    public static bool TryId(string? value, out long id)
    {
        id = 0;
        if (value is null || value.Length is < 49 or > 64 || !value.StartsWith("te1.", StringComparison.Ordinal))
        { return false; }
        var separator = value.IndexOf('.', 4);
        if (separator is < 5 or > 20 || value.Length - separator != 44 ||
            !long.TryParse(value.AsSpan(4, separator - 4), NumberStyles.None, CultureInfo.InvariantCulture, out id) ||
            id is < 1 or > 9007199254740991L || value[4] == '0')
        { return false; }
        return value.AsSpan(separator + 1).IndexOfAnyExcept("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_".AsSpan()) < 0;
    }
}
