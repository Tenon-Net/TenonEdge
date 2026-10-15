using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TenonEdge.Abstractions.Contracts;

/// <summary>冻结在指纹摘要中的非秘密接收方信息，不从当前连接补造历史身份。
/// Recorded=false 表示未记录/不可知；Recorded=true 且值为 null 表示没有可见账户或未使用客户端证书。
/// 证书字段指客户端身份，不是未经连接验证的服务端证书。摘要不是所有权的技术证明。</summary>
public sealed record OutputRecipientIdentity(string Protocol, string Address, string Authentication,
    string? Account, bool AccountRecorded, string? ClientCertificateSubject, bool ClientCertificateRecorded)
{
    private const string Prefix = "recipient:v1:";
    public const int MaximumSummaryBytes = 32 * 1024;
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        PropertyNameCaseInsensitive = false,
        MaxDepth = 4,
    };

    public static OutputRecipientIdentity Mqtt(string address, bool usePassword, MqttCredentialIdentity? identity) =>
        new("mqtt", address, usePassword ? "password" : "anonymous", usePassword ? identity?.Username : null,
            !usePassword || identity is not null, null, true);

    public static OutputRecipientIdentity Http(string address, HttpCredentialIdentity? identity) =>
        identity?.Kind == HttpCredentialKind.ClientCertificate
        ? new("http", address, identity.Kind.ToString(), identity.CertificateSubject, identity.CertificateSubject is not null,
            identity.CertificateSubject, identity.CertificateSubject is not null)
        :
        new("http", address, identity?.Kind.ToString() ?? "none", identity?.Kind == HttpCredentialKind.Basic ? identity.Username : null,
            identity?.Kind != HttpCredentialKind.Basic || identity.Username is not null, null, true);

    /// <summary>有版本且可读的 JSON 摘要。仍使用既有 fingerprint_summary 列，迁移时与指纹一起替换，
    /// 不污染必须原样保留的 protocol_metadata，也不更改指纹算法。</summary>
    public string Freeze()
    {
        if (!Valid())
        { throw new ArgumentException("接收方非秘密身份无效。"); }
        var summary = Prefix + JsonSerializer.Serialize(this, Options);
        if (Encoding.UTF8.GetByteCount(summary) > MaximumSummaryBytes)
        { throw new ArgumentException("接收方非秘密摘要超过上限。"); }
        return summary;
    }

    /// <summary>只识别本网关已写出的格式；未知版本、歧义或损坏返回 null，不能据当前连接推断旧协议。
    /// 旧 HTTP 摘要没有账户，旧摘要没有证书字段；如实保留未知，不冒充匿名或当前账户。</summary>
    public static OutputRecipientIdentity? Read(string? summary)
    {
        if (string.IsNullOrEmpty(summary) || summary.Length > MaximumSummaryBytes || Encoding.UTF8.GetByteCount(summary) > MaximumSummaryBytes)
        { return null; }
        try
        {
            OutputRecipientIdentity? identity;
            if (summary.StartsWith(Prefix, StringComparison.Ordinal))
            {
                using var document = JsonDocument.Parse(summary[Prefix.Length..], new() { MaxDepth = 4 });
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                { return null; }
                var properties = document.RootElement.EnumerateObject().Select(property => property.Name).ToArray();
                if (properties.Distinct(StringComparer.Ordinal).Count() != properties.Length)
                { return null; }
                identity = document.RootElement.Deserialize<OutputRecipientIdentity>(Options);
            }
            else
            { identity = ReadLegacy(summary); }
            return identity?.Valid() == true ? identity : null;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException)
        { return null; }
    }

    private static OutputRecipientIdentity? ReadLegacy(string summary)
    {
        const string authentication = "; authentication=";
        const string account = "; account=";
        // HTTP 的合法基础路径可以包含分号，认证信息只取末尾的旧固定后缀。
        if (summary.StartsWith("http://", StringComparison.Ordinal) || summary.StartsWith("https://", StringComparison.Ordinal))
        {
            var at = summary.LastIndexOf(authentication, StringComparison.Ordinal);
            if (at < 0)
            { return null; }
            var kind = summary[(at + authentication.Length)..];
            return new("http", summary[..at], kind, null, kind != "Basic", null, false);
        }
        if (summary.StartsWith("mqtt://", StringComparison.Ordinal) || summary.StartsWith("mqtts://", StringComparison.Ordinal))
        {
            var at = summary.IndexOf(authentication, StringComparison.Ordinal);
            var accountAt = at < 0 ? -1 : summary.IndexOf(account, at + authentication.Length, StringComparison.Ordinal);
            if (accountAt < 0)
            { return null; }
            var kind = summary[(at + authentication.Length)..accountAt];
            var username = JsonSerializer.Deserialize<string>(summary[(accountAt + account.Length)..]);
            if (username is null || kind == "anonymous" && username.Length != 0)
            { return null; }
            // 老版本把凭证身份缺失也写成空串，无法区分合法空用户名，不据此声称已知。
            return new("mqtt", summary[..at], kind, kind == "password" && username.Length > 0 ? username : null,
                kind == "anonymous" || username.Length > 0, null, false);
        }
        return null;
    }

    private bool Valid()
    {
        // JSON 每个 UTF-16 码元至多六个 ASCII 转义字节；固定键名/标点另留512字节。
        // 序列化前检查最坏上界，避免先申请超过已预约的暂存、事后才因32 KiB超限拒绝。
        var characters = (long)(Protocol?.Length ?? 0) + (Address?.Length ?? 0) + (Authentication?.Length ?? 0) +
            (Account?.Length ?? 0) + (ClientCertificateSubject?.Length ?? 0);
        if (6 * characters + 512 > MaximumSummaryBytes)
        { return false; }
        if (Address is null || Address.Any(char.IsControl) || !Uri.TryCreate(Address, UriKind.Absolute, out var uri) ||
            !uri.IsWellFormedOriginalString() || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            string.IsNullOrEmpty(uri.Host) || uri.Port is 0 or > 65535 ||
            !AccountRecorded && Account is not null || !ClientCertificateRecorded && ClientCertificateSubject is not null)
        { return false; }
        return Protocol switch
        {
            "mqtt" => uri.Scheme is "mqtt" or "mqtts" && uri.Port > 0 && uri.AbsolutePath is "" or "/" &&
                (Authentication == "password" && (!AccountRecorded || Account is not null) || Authentication == "anonymous" && AccountRecorded && Account is null),
            "http" => uri.Scheme is "http" or "https" &&
                (Authentication == "Basic" && (!AccountRecorded || Account is not null) ||
                    Authentication == "ClientCertificate" && AccountRecorded && ClientCertificateRecorded &&
                    !string.IsNullOrWhiteSpace(ClientCertificateSubject) && Account == ClientCertificateSubject ||
                    Authentication is "none" or "Bearer" or "ApiKeyHeader" or "ApiKeyQuery" && AccountRecorded && Account is null),
            _ => false,
        };
    }
}

/// <summary>由适配器一次读取非秘密凭证描述，同时计算指纹和可读摘要，避免拼接两次读取的不同账户。</summary>
public sealed record OutputTargetIdentity(string Fingerprint, OutputRecipientIdentity Recipient)
{
    public string Summary => Recipient.Freeze();
}
