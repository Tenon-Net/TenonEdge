using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace TenonEdge.IntegrationTests.Probes.Mqtt;

/// <summary>
/// T0-11 的测试 CA（验证代码）：在内存中生成根证书，并为 Broker 签发服务器证书（SAN 为 localhost 与 127.0.0.1）。
/// 只用于测试，每次运行重新生成，私钥不落入仓库。
/// </summary>
internal sealed class TestCertificateAuthority : IDisposable
{
    private readonly ECDsa _key;
    private readonly X509Certificate2 _root;

    private TestCertificateAuthority(ECDsa key, X509Certificate2 root)
    {
        _key = key;
        _root = root;
    }

    /// <summary>不含私钥的根证书，用作客户端的信任根。</summary>
    public X509Certificate2 RootCertificate => X509CertificateLoader.LoadCertificate(_root.RawData);

    public static TestCertificateAuthority Create(string name)
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        var now = DateTimeOffset.UtcNow;
        return new TestCertificateAuthority(key, request.CreateSelfSigned(now.AddHours(-1), now.AddDays(2)));
    }

    /// <summary>签发服务器证书，返回 PEM 格式的证书与 PKCS#8 私钥，以及证书的 SHA-256 指纹。</summary>
    public (string CertificatePem, string KeyPem, string Sha256Fingerprint) IssueServerCertificate(string dnsName)
    {
        using var serverKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={dnsName}", serverKey, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        var alternativeNames = new SubjectAlternativeNameBuilder();
        alternativeNames.AddDnsName(dnsName);
        alternativeNames.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(alternativeNames.Build());
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(_root, true, false));

        var now = DateTimeOffset.UtcNow;
        using var certificate = request.Create(_root, now.AddHours(-1), now.AddDays(1), RandomNumberGenerator.GetBytes(16));
        return (certificate.ExportCertificatePem(), serverKey.ExportPkcs8PrivateKeyPem(), certificate.GetCertHashString(HashAlgorithmName.SHA256));
    }

    public string ExportRootPem() => _root.ExportCertificatePem();

    public void Dispose()
    {
        _root.Dispose();
        _key.Dispose();
    }
}
