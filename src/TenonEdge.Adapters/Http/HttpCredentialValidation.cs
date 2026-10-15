using TenonEdge.Abstractions.Contracts;
using TenonEdge.Adapters.Diagnostics;

namespace TenonEdge.Adapters.Http;

/// <summary>管理写入与发送共用的凭证格式检查；错误说明不包含凭证内容。</summary>
public static class HttpCredentialValidation
{
    public static void Validate(HttpCredentialKind kind, string? username, string? name, string secret)
    {
        if (kind == HttpCredentialKind.ClientCertificate)
        {
            if (string.IsNullOrEmpty(secret) || secret.Length > 4 * ((TlsClientCertificate.MaximumPkcs12Bytes + 2) / 3) || username is not null || name is not null)
            { throw new ArgumentException("HTTP 客户端证书凭证无效。"); }
            return; // 容器/私钥/主题在 TLS 所有者创建时再次校验，不能作为请求头发送。
        }
        if (secret is null || secret.Length > 4096 || secret.IndexOfAny(['\r', '\n', '\0']) >= 0)
        { throw new ArgumentException("HTTP 凭证无效。"); }
        switch (kind)
        {
            case HttpCredentialKind.Basic:
                if (string.IsNullOrEmpty(username) || username.Length > 256 || username.Any(char.IsControl) || username.Contains(':', StringComparison.Ordinal) || name is not null)
                { throw new ArgumentException("HTTP Basic 须提供有效用户名，不能提供 API Key 名称。"); }
                break;
            case HttpCredentialKind.Bearer:
                if (username is not null || name is not null || secret.Length == 0 ||
                    secret.TrimEnd('=').Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '.' or '_' or '~' or '+' or '/')) || secret[0] == '=')
                { throw new ArgumentException("HTTP Bearer 须提供有效令牌，不能提供用户名或 API Key 名称。"); }
                break;
            case HttpCredentialKind.ApiKeyHeader:
                if (username is not null || string.IsNullOrEmpty(name) || name.Length > 128 || secret.Length == 0)
                { throw new ArgumentException("HTTP API Key 须提供名称和值，不能提供用户名。"); }
                try
                { HttpOutputSettings.ValidateHeader(name, secret, credential: true); }
                catch (Exception exception) when (exception is ArgumentException or FormatException)
                { throw new ArgumentException("HTTP API Key 请求头无效。", exception); }
                break;
            case HttpCredentialKind.ApiKeyQuery:
                if (username is not null || string.IsNullOrWhiteSpace(name) || name.Length > 128 || name.Any(char.IsControl) || secret.Length == 0)
                { throw new ArgumentException("HTTP API Key 查询参数须提供有效名称和值，不能提供用户名。"); }
                break;
            default:
                throw new ArgumentException("HTTP 凭证类型无效。");
        }
    }
}
