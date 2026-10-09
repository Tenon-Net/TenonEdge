using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;
using TenonEdge.Host.Storage;

namespace TenonEdge.Host.Security;

/// <summary>
/// HTTPS 使用的自签证书（总设计 §10.3，T0-06）：首次启动时生成，属于数据目录中的“身份与本地秘密文件”，随备份迁移。
/// <list type="bullet">
/// <item>RSA 2048、SHA-256，仅用于服务器认证；主题含 NodeId 短码；使用者可选名称为 localhost、主机名、回环地址和生成时本机的单播地址。
/// IP 变化后证书与地址不符是已知代价（总设计 §10.3），上传正式证书属于 T5-01。</item>
/// <item>有效期 825 天（部分客户端不接受更长的服务器证书）；启动时剩余不足 30 天即重新生成，浏览器会提示证书变化。</item>
/// <item>私钥属于秘密：PKCS#12 内容经 Data Protection 保护后写入 identity 目录（目录 700、文件 600），不以明文落盘。</item>
/// <item>原文件无法解密（例如密钥环丢失）时重新生成并告警，保证 HTTPS 仍可用；这类情况也意味着其他秘密无法解密，须排查密钥环。</item>
/// </list>
/// </summary>
internal sealed partial class HttpsCertificateStore(
    DataDirectoryLock dataDirectory,
    IDataProtectionProvider protection,
    GatewayIdentity identity,
    TimeProvider time,
    ILogger<HttpsCertificateStore> logger) : IDisposable
{
    public const string IdentityDirectoryName = "identity";

    public const string CertificateFileName = "https-certificate.pfx.protected";

    public static readonly TimeSpan Validity = TimeSpan.FromDays(825);

    public static readonly TimeSpan RenewBefore = TimeSpan.FromDays(30);

    private const string ServerAuthenticationOid = "1.3.6.1.5.5.7.3.1";
    private const int MaximumAddresses = 32;

    private readonly Lock _lock = new();
    private X509Certificate2? _certificate;

    public string FilePath => Path.Combine(dataDirectory.Directory.Root, IdentityDirectoryName, CertificateFileName);

    /// <summary>当前使用的证书；首次访问时从数据目录加载，缺少、无法解密或即将到期时重新生成。</summary>
    public X509Certificate2 Certificate
    {
        get
        {
            lock (_lock)
            {
                return _certificate ??= LoadOrCreate();
            }
        }
    }

    public void Dispose() => _certificate?.Dispose();

    private X509Certificate2 LoadOrCreate()
    {
        var now = time.GetUtcNow();
        var protector = protection.CreateProtector(SecretProtection.Purposes.HttpsCertificate);
        if (File.Exists(FilePath))
        {
            try
            {
                var existing = X509CertificateLoader.LoadPkcs12(protector.Unprotect(File.ReadAllBytes(FilePath)), password: null);
                var expiresAt = existing.NotAfter.ToUniversalTime();
                if (expiresAt - now.UtcDateTime > RenewBefore)
                {
                    var fingerprint = existing.GetCertHashString(HashAlgorithmName.SHA256);
                    LogCertificateLoaded(logger, fingerprint, expiresAt);
                    return existing;
                }

                LogCertificateRenewing(logger, expiresAt);
                existing.Dispose();
            }
            catch (CryptographicException exception)
            {
                LogCertificateUnreadable(logger, FilePath, exception.Message);
            }
        }

        var created = Generate(now);
        Save(protector.Protect(created.Export(X509ContentType.Pkcs12)));
        var createdFingerprint = created.GetCertHashString(HashAlgorithmName.SHA256);
        var createdExpiresAt = created.NotAfter.ToUniversalTime();
        LogCertificateCreated(logger, createdFingerprint, createdExpiresAt);
        return created;
    }

    private X509Certificate2 Generate(DateTimeOffset now)
    {
        using var key = RSA.Create(2048);
        var subject = new X500DistinguishedName($"CN=TenonEdge {identity.NodeId[..8]}");
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
            critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(ServerAuthenticationOid)], critical: false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));
        request.CertificateExtensions.Add(BuildSubjectAlternativeNames());

        // 开始时间提前一天，容许客户端与网关之间的时钟偏差。
        using var selfSigned = request.CreateSelfSigned(now.AddDays(-1), now.Add(Validity));
        return X509CertificateLoader.LoadPkcs12(selfSigned.Export(X509ContentType.Pkcs12), password: null);
    }

    private static X509Extension BuildSubjectAlternativeNames()
    {
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        var hostName = Dns.GetHostName();
        if (Uri.CheckHostName(hostName) == UriHostNameType.Dns && !string.Equals(hostName, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            names.AddDnsName(hostName);
        }

        names.AddIpAddress(IPAddress.Loopback);
        names.AddIpAddress(IPAddress.IPv6Loopback);
        foreach (var address in LocalUnicastAddresses())
        {
            names.AddIpAddress(address);
        }

        return names.Build();
    }

    internal static List<IPAddress> LocalUnicastAddresses(Func<NetworkInterface[]>? enumerate = null)
    {
        try
        {
            return (enumerate ?? NetworkInterface.GetAllNetworkInterfaces)()
                .Where(networkInterface => networkInterface.OperationalStatus == OperationalStatus.Up
                    && networkInterface.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(networkInterface => networkInterface.GetIPProperties().UnicastAddresses)
                .Select(unicast => unicast.Address)
                .Where(address => address.AddressFamily == AddressFamily.InterNetwork
                    || (address.AddressFamily == AddressFamily.InterNetworkV6 && !address.IsIPv6LinkLocal))
                .Distinct()
                .Take(MaximumAddresses)
                .ToList();
        }
        catch (NetworkInformationException)
        {
            return [];
        }
        catch (ArgumentException exception) when (exception.ParamName == "address")
        {
            // LinuxNetworkInterface can fail while decoding an OS-provided address.
            // Local IP SANs are best-effort, like NetworkInformationException above;
            // localhost, host name and loopback SANs remain in the certificate.
            return [];
        }
    }

    private void Save(byte[] protectedContent)
    {
        var directory = Path.GetDirectoryName(FilePath)!;
        PrivateFileSystem.CreateDirectory(directory);
        var temporary = FilePath + ".tmp";
        using (var file = new FileStream(temporary, PrivateFileSystem.Options(FileMode.Create, FileAccess.Write, FileShare.None)))
        {
            file.Write(protectedContent);
            file.Flush(flushToDisk: true);
        }

        File.Move(temporary, FilePath, overwrite: true);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "已生成 HTTPS 自签证书，SHA-256 指纹 {Fingerprint}，有效期至 {NotAfter:O}。")]
    private static partial void LogCertificateCreated(ILogger logger, string fingerprint, DateTime notAfter);

    [LoggerMessage(Level = LogLevel.Information, Message = "HTTPS 证书 SHA-256 指纹 {Fingerprint}，有效期至 {NotAfter:O}。")]
    private static partial void LogCertificateLoaded(ILogger logger, string fingerprint, DateTime notAfter);

    [LoggerMessage(Level = LogLevel.Warning, Message = "HTTPS 证书将于 {NotAfter:O} 到期，已重新生成；浏览器会提示证书变化。")]
    private static partial void LogCertificateRenewing(ILogger logger, DateTime notAfter);

    [LoggerMessage(Level = LogLevel.Warning, Message = "无法解密 HTTPS 证书文件 {Path}（{Reason}），已重新生成。密钥环可能已丢失或被替换，其他秘密同样无法解密，请检查数据目录。")]
    private static partial void LogCertificateUnreadable(ILogger logger, string path, string reason);
}
