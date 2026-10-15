using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using TenonEdge.Abstractions.Contracts;

namespace TenonEdge.Adapters.Diagnostics;

/// <summary>只用于配置人员/管理员的显式连接测试。公开证书不含私钥；不能把来自对方的主题/颁发者当作可信身份。</summary>
public sealed record ServerCertificateInfo(string Subject, string Issuer, DateTimeOffset NotBefore, DateTimeOffset NotAfter,
    string Sha256Fingerprint, string CertificateDerBase64)
{
    public override string ToString() => $"Server certificate ({Sha256Fingerprint}; names and certificate omitted)";
}

/// <summary>两协议、运行及诊断共用的服务器信任判断。固定证书只替代信任锚，不豁免名称、时间、用途或其他链错误。
/// 自定义信任离线建链，禁止 AIA/CRL 网络下载；已有平台 Revoked 结果仍拒绝。未设置时保持平台默认验证结果。</summary>
public static class ServerCertificateTrust
{
    public const int MaximumCertificateBytes = 32 * 1024;
    private const string ServerAuthentication = "1.3.6.1.5.5.7.3.1";

    internal static X509ChainPolicy OfflineChainPolicy()
    {
        var policy = new X509ChainPolicy { DisableCertificateDownloads = true, RevocationMode = X509RevocationMode.NoCheck };
        policy.ApplicationPolicy.Add(new Oid(ServerAuthentication));
        return policy;
    }

    public static ServerCertificateInfo? Describe(X509Certificate? certificate)
    {
        if (certificate is null)
        { return null; }
        var bytes = certificate.GetRawCertData();
        if (bytes.Length > MaximumCertificateBytes)
        { return null; }
        using var value = X509CertificateLoader.LoadCertificate(bytes);
        return new(Label(value.Subject), Label(value.Issuer), value.NotBefore.ToUniversalTime(), value.NotAfter.ToUniversalTime(),
            value.GetCertHashString(HashAlgorithmName.SHA256), Convert.ToBase64String(bytes));
    }

    /// <summary>校验管理入口提供的单个 DER 证书；不能用 PFX/私钥容器或非 CA 叶证书冒充 CA。</summary>
    public static ServerCertificateInfo Validate(TlsTrustSnapshot trust)
    {
        ArgumentNullException.ThrowIfNull(trust);
        using var certificate = Load(trust);
        ValidateFields(trust, certificate);
        return Describe(certificate)!;
    }

    private static void ValidateFields(TlsTrustSnapshot trust, X509Certificate2 certificate)
    {
        if (!Enum.IsDefined(trust.Mode) || string.IsNullOrWhiteSpace(trust.Version) || trust.Version.Length > 128 ||
            certificate.HasPrivateKey || trust.Mode == TlsTrustMode.CertificateAuthority && !IsAuthority(certificate))
        { throw new ArgumentException("TLS 信任模式、版本或证书用途无效。"); }
    }

    /// <returns>null 表示接受；其他值是稳定的脱敏失败分类。</returns>
    public static string? Evaluate(X509Certificate? certificate, X509Chain? platformChain, SslPolicyErrors errors, TlsTrustSnapshot? trust)
    {
        if (certificate is null || (errors & SslPolicyErrors.RemoteCertificateNotAvailable) != 0)
        { return "certificate.missing"; }
        if (trust is null)
        { return CertificateFailure.Classify(errors, platformChain, DateTime.UtcNow); }
        if ((errors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0)
        { return "certificate.nameMismatch"; }
        if ((errors & ~(SslPolicyErrors.RemoteCertificateChainErrors | SslPolicyErrors.RemoteCertificateNameMismatch)) != 0)
        { return "certificate.invalid"; }
        try
        {
            using var anchor = Load(trust);
            ValidateFields(trust, anchor);
            var raw = certificate.GetRawCertData();
            if (raw.Length > MaximumCertificateBytes)
            { return "certificate.tooLarge"; }
            using var leaf = X509CertificateLoader.LoadCertificate(raw);
            if (trust.Mode == TlsTrustMode.PinnedCertificate &&
                !CryptographicOperations.FixedTimeEquals(SHA256.HashData(raw), anchor.GetCertHash(HashAlgorithmName.SHA256)))
            { return "certificate.changed"; }
            var now = DateTime.UtcNow;
            if (leaf.NotAfter.ToUniversalTime() < now || anchor.NotAfter.ToUniversalTime() < now)
            { return "certificate.expired"; }
            if (leaf.NotBefore.ToUniversalTime() > now || anchor.NotBefore.ToUniversalTime() > now)
            { return "certificate.notYetValid"; }
            if (platformChain?.ChainStatus.Any(item => (item.Status & X509ChainStatusFlags.Revoked) != 0) == true)
            { return "certificate.revoked"; }

            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(anchor);
            chain.ChainPolicy.DisableCertificateDownloads = true;
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.ApplicationPolicy.Add(new Oid(ServerAuthentication));
            // 叶证书固定模式无需对方交付签发链，但不能忽略用途、签名、时间、约束等其他错误。
            // CA 模式只允许链终止于上传的 CA（含明确上传的中间 CA），不回退到系统根。
            var intermediateAnchor = trust.Mode == TlsTrustMode.CertificateAuthority &&
                !anchor.SubjectName.RawData.AsSpan().SequenceEqual(anchor.IssuerName.RawData);
            if (trust.Mode == TlsTrustMode.PinnedCertificate || intermediateAnchor)
            { chain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority; }
            if (platformChain is not null)
            {
                foreach (var element in platformChain.ChainElements.Cast<X509ChainElement>().Skip(1))
                {
                    chain.ChainPolicy.ExtraStore.Add(element.Certificate);
                    // 上传的中间 CA 本身就是本连接的信任锚，不再依赖它的上级根。
                    if (intermediateAnchor && element.Certificate.RawData.AsSpan().SequenceEqual(anchor.RawData))
                    { break; }
                }
            }
            var built = chain.Build(leaf);
            var anchored = !intermediateAnchor || chain.ChainElements.Count > 0 &&
                chain.ChainElements[^1].Certificate.RawData.AsSpan().SequenceEqual(anchor.RawData);
            var allowed = trust.Mode == TlsTrustMode.PinnedCertificate || intermediateAnchor
                ? X509ChainStatusFlags.UntrustedRoot | X509ChainStatusFlags.PartialChain : X509ChainStatusFlags.NoError;
            if (built && anchored && chain.ChainStatus.All(item => (item.Status & ~allowed) == 0))
            { return null; }
            return CertificateFailure.Classify(SslPolicyErrors.RemoteCertificateChainErrors, chain, now);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or CryptographicException)
        { return "certificate.trustInvalid"; }
    }

    private static X509Certificate2 Load(TlsTrustSnapshot trust)
    {
        if (string.IsNullOrEmpty(trust.CertificateDerBase64) || trust.CertificateDerBase64.Length > 4 * ((MaximumCertificateBytes + 2) / 3))
        { throw new ArgumentException("TLS 证书为空或超限。"); }
        var bytes = Convert.FromBase64String(trust.CertificateDerBase64);
        if (bytes.Length > MaximumCertificateBytes)
        { throw new ArgumentException("TLS 证书超限。"); }
        var result = X509CertificateLoader.LoadCertificate(bytes);
        if (!bytes.AsSpan().SequenceEqual(result.RawData))
        { result.Dispose(); throw new ArgumentException("TLS 证书须为单个完整 DER 公钥证书。"); }
        return result;
    }

    private static bool IsAuthority(X509Certificate2 certificate) =>
        certificate.Extensions.OfType<X509BasicConstraintsExtension>().Any(value => value.CertificateAuthority) &&
        certificate.Extensions.OfType<X509KeyUsageExtension>().All(value => (value.KeyUsages & X509KeyUsageFlags.KeyCertSign) != 0);

    private static string Label(string value) => new(value.Take(2048).Select(character => char.IsControl(character) ? ' ' : character).ToArray());
}
