using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using TenonEdge.Host.Security;
using TenonEdge.Host.Storage;
using TenonEdge.IntegrationTests.Infrastructure;

namespace TenonEdge.IntegrationTests.Security;

/// <summary>
/// HTTPS 自签证书（T0-06）：首次生成的证书属性；私钥经 Data Protection 保护后落盘；重启后复用；临近到期或无法解密时重新生成。
/// </summary>
public sealed class HttpsCertificateStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void GeneratedCertificateIsASelfSignedServerCertificateStoredOnlyInProtectedForm()
    {
        using var store = TestConfigStore.Create();
        store.Initialize();
        var identity = GatewayIdentity.LoadOrCreate(store.Database, out _);
        using var certificates = CreateStore(store, identity, new ManualTimeProvider(Now));

        var certificate = certificates.Certificate;

        Assert.Equal(certificate.SubjectName.Name, certificate.IssuerName.Name);
        Assert.Contains(identity.NodeId[..8], certificate.Subject, StringComparison.Ordinal);
        Assert.True(certificate.HasPrivateKey);
        Assert.Equal(2048, certificate.GetRSAPublicKey()!.KeySize);
        Assert.Equal("sha256RSA", certificate.SignatureAlgorithm.FriendlyName);
        Assert.Equal(Now.UtcDateTime.AddDays(-1), certificate.NotBefore.ToUniversalTime(), TimeSpan.FromSeconds(1));
        Assert.Equal(Now.UtcDateTime.Add(HttpsCertificateStore.Validity), certificate.NotAfter.ToUniversalTime(), TimeSpan.FromSeconds(1));
        var usage = Assert.Single(certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>());
        Assert.Equal("1.3.6.1.5.5.7.3.1", Assert.Single(usage.EnhancedKeyUsages.Cast<System.Security.Cryptography.Oid>()).Value);
        var names = Assert.Single(certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>());
        Assert.Contains("localhost", names.EnumerateDnsNames());
        Assert.Contains(IPAddress.Loopback, names.EnumerateIPAddresses());
        Assert.Contains(IPAddress.IPv6Loopback, names.EnumerateIPAddresses());

        // 落盘的是受保护的内容：不经 Data Protection 解除保护，无法当作 PKCS#12 读取。
        var stored = File.ReadAllBytes(certificates.FilePath);
        Assert.ThrowsAny<CryptographicException>(() => X509CertificateLoader.LoadPkcs12(stored, password: null));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(PrivateFileSystem.FileMode, File.GetUnixFileMode(certificates.FilePath));
            Assert.Equal(PrivateFileSystem.DirectoryMode, File.GetUnixFileMode(Path.GetDirectoryName(certificates.FilePath)!));
        }
    }

    [Fact]
    public void StoredCertificateIsReusedByTheNextStart()
    {
        using var store = TestConfigStore.Create();
        store.Initialize();
        var identity = GatewayIdentity.LoadOrCreate(store.Database, out _);
        string thumbprint;
        using (var first = CreateStore(store, identity, new ManualTimeProvider(Now)))
        {
            thumbprint = first.Certificate.Thumbprint;
        }

        using var second = CreateStore(store, identity, new ManualTimeProvider(Now + TimeSpan.FromDays(100)));
        Assert.Equal(thumbprint, second.Certificate.Thumbprint);
    }

    [Fact]
    public void CertificateCloseToExpiryIsRegenerated()
    {
        using var store = TestConfigStore.Create();
        store.Initialize();
        var identity = GatewayIdentity.LoadOrCreate(store.Database, out _);
        X509Certificate2 original;
        using (var first = CreateStore(store, identity, new ManualTimeProvider(Now)))
        {
            original = X509CertificateLoader.LoadCertificate(first.Certificate.RawData);
        }

        var almostExpired = new DateTimeOffset(original.NotAfter.ToUniversalTime()) - HttpsCertificateStore.RenewBefore + TimeSpan.FromDays(1);
        using var renewed = CreateStore(store, identity, new ManualTimeProvider(almostExpired));

        Assert.NotEqual(original.Thumbprint, renewed.Certificate.Thumbprint);
        Assert.True(renewed.Certificate.NotAfter.ToUniversalTime() > original.NotAfter.ToUniversalTime());
        original.Dispose();
    }

    [Fact]
    public void UndecryptableCertificateFileIsReplacedWithANewCertificate()
    {
        using var store = TestConfigStore.Create();
        store.Initialize();
        var identity = GatewayIdentity.LoadOrCreate(store.Database, out _);
        string path;
        using (var first = CreateStore(store, identity, new ManualTimeProvider(Now)))
        {
            _ = first.Certificate;
            path = first.FilePath;
        }

        File.WriteAllBytes(path, RandomNumberGenerator.GetBytes(512));

        using var replaced = CreateStore(store, identity, new ManualTimeProvider(Now));
        Assert.True(replaced.Certificate.HasPrivateKey);
        using var reloaded = CreateStore(store, identity, new ManualTimeProvider(Now));
        Assert.Equal(replaced.Certificate.Thumbprint, reloaded.Certificate.Thumbprint);
    }

    private static HttpsCertificateStore CreateStore(TestConfigStore store, GatewayIdentity identity, TimeProvider time)
    {
        var keyRing = new DirectoryInfo(Path.Combine(store.DataDirectoryPath, SecretProtection.KeyRingDirectoryName));
        var protection = DataProtectionProvider.Create(keyRing, setup => setup.SetApplicationName(SecretProtection.ApplicationName));
        return new HttpsCertificateStore(store.DataDirectoryLock, protection, identity, time, NullLogger<HttpsCertificateStore>.Instance);
    }
}
