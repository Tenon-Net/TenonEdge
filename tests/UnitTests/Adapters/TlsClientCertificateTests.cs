using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using TenonEdge.Adapters.Diagnostics;

namespace TenonEdge.UnitTests.Adapters;

public sealed class TlsClientCertificateTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void PemAndPkcs12RetainOnlyOneMatchingIdentityAndRoundTripInMemory(bool rsa, bool pfx)
    {
        using var key = rsa ? (AsymmetricAlgorithm)RSA.Create(2048) : ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var leaf = Certificate(key);
        using var material = pfx
            ? TlsClientCertificate.FromPkcs12(leaf.Export(X509ContentType.Pfx, "fixture-container-password"), "fixture-container-password", Now)
            : TlsClientCertificate.FromPem(leaf.ExportCertificatePem(), key.ExportPkcs8PrivateKeyPem(), Now);
        Assert.Equal(leaf.Subject, material.Info.Subject);
        Assert.Equal(leaf.Issuer, material.Info.Issuer);
        Assert.Equal(leaf.GetCertHashString(HashAlgorithmName.SHA256), material.Info.Sha256Fingerprint);
        Assert.Equal(leaf.NotAfter.ToUniversalTime(), material.Info.NotAfter.UtcDateTime);
        Assert.DoesNotContain("fixture-container-password", material.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE KEY", material.ToString(), StringComparison.Ordinal);
        var exported = material.ExportPkcs12();
        try
        {
            using var reloaded = TlsClientCertificate.FromPkcs12(exported, [], Now);
            Assert.Equal(material.Info, reloaded.Info);
            Assert.Same(reloaded.Context(Now), reloaded.Context(Now));
            Assert.True(reloaded.Context(Now).TargetCertificate.HasPrivateKey);
        }
        finally { CryptographicOperations.ZeroMemory(exported); }
    }

    [Theory]
    [InlineData("RSA PRIVATE KEY")]
    [InlineData("EC PRIVATE KEY")]
    public void TraditionalUnencryptedPemKeysAreAccepted(string label)
    {
        using var key = label == "RSA PRIVATE KEY" ? (AsymmetricAlgorithm)RSA.Create(2048) : ECDsa.Create();
        using var leaf = Certificate(key);
        var pem = key is RSA rsa ? rsa.ExportRSAPrivateKeyPem() : ((ECDsa)key).ExportECPrivateKeyPem();
        using var material = TlsClientCertificate.FromPem(leaf.ExportCertificatePem(), pem, Now);
        Assert.Equal(leaf.Subject, material.Info.Subject);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("size")]
    [InlineData("wrongPassword")]
    [InlineData("longPassword")]
    [InlineData("nulPassword")]
    [InlineData("trailing")]
    [InlineData("certificateOnly")]
    [InlineData("publicPfx")]
    [InlineData("twoKeys")]
    [InlineData("tooManyCertificates")]
    [InlineData("iterations")]
    [InlineData("extraLeaf")]
    [InlineData("duplicate")]
    public void InvalidPkcs12IsRejectedWithoutExposingSecrets(string scenario)
    {
        using var key = ECDsa.Create();
        using var leaf = Certificate(key);
        using var publicLeaf = X509CertificateLoader.LoadCertificate(leaf.RawData);
        using var otherKey = ECDsa.Create();
        using var other = Certificate(otherKey, subject: "CN=other");
        using var otherPublic = X509CertificateLoader.LoadCertificate(other.RawData);
        var password = "fixture-private-password";
        var data = scenario switch
        {
            "empty" => [],
            "size" => new byte[TlsClientCertificate.MaximumPkcs12Bytes + 1],
            "trailing" => [.. leaf.Export(X509ContentType.Pfx, password), 0],
            "certificateOnly" => leaf.RawData,
            "publicPfx" => publicLeaf.Export(X509ContentType.Pfx, password),
            "twoKeys" => new X509Certificate2Collection { leaf, other }.Export(X509ContentType.Pfx, password)!,
            "extraLeaf" => new X509Certificate2Collection { leaf, otherPublic }.Export(X509ContentType.Pfx, password)!,
            "duplicate" => new X509Certificate2Collection { leaf, publicLeaf }.Export(X509ContentType.Pfx, password)!,
            "tooManyCertificates" => new X509Certificate2Collection(Enumerable.Repeat(publicLeaf, 8).Prepend(leaf).ToArray())
                .Export(X509ContentType.Pfx, password)!,
            "iterations" => leaf.ExportPkcs12(new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 100_001), password),
            _ => leaf.Export(X509ContentType.Pfx, password),
        };
        var providedPassword = scenario switch
        {
            "wrongPassword" => "fixture-wrong-password",
            "longPassword" => new string('x', TlsClientCertificate.MaximumPasswordCharacters + 1),
            "nulPassword" => "fixture\0password",
            _ => password,
        };
        try
        {
            var exception = Assert.Throws<ArgumentException>(() => TlsClientCertificate.FromPkcs12(data, providedPassword, Now));
            Assert.Null(exception.InnerException);
            Assert.DoesNotContain("fixture", exception.ToString(), StringComparison.Ordinal);
        }
        finally { CryptographicOperations.ZeroMemory(data); }
    }

    [Theory]
    [InlineData("emptyCertificate")]
    [InlineData("emptyKey")]
    [InlineData("certificateSize")]
    [InlineData("keySize")]
    [InlineData("wrongKey")]
    [InlineData("twoKeys")]
    [InlineData("encryptedKey")]
    [InlineData("prefix")]
    [InlineData("suffix")]
    [InlineData("keyInCertificates")]
    [InlineData("certificateInKey")]
    [InlineData("trailingDer")]
    [InlineData("trailingKeyDer")]
    [InlineData("base64")]
    [InlineData("tooManyCertificates")]
    public void InvalidPemIsRejectedRatherThanSilentlyIgnoringAdditionalContent(string scenario)
    {
        using var key = ECDsa.Create();
        using var leaf = Certificate(key);
        using var other = ECDsa.Create();
        var certificates = scenario switch
        {
            "emptyCertificate" => "",
            "certificateSize" => new string('x', TlsClientCertificate.MaximumPemCharacters + 1),
            "prefix" => "unexpected text\n" + leaf.ExportCertificatePem(),
            "suffix" => leaf.ExportCertificatePem() + "\nunexpected text",
            "keyInCertificates" => leaf.ExportCertificatePem() + "\n" + key.ExportPkcs8PrivateKeyPem(),
            "trailingDer" => PemEncoding.WriteString("CERTIFICATE", [.. leaf.RawData, 0]),
            "base64" => "-----BEGIN CERTIFICATE-----\n!notbase64!\n-----END CERTIFICATE-----",
            "tooManyCertificates" => string.Join('\n', Enumerable.Repeat(leaf.ExportCertificatePem(), 9)),
            _ => leaf.ExportCertificatePem(),
        };
        var privateKey = scenario switch
        {
            "emptyKey" => "",
            "keySize" => new string('x', TlsClientCertificate.MaximumPrivateKeyCharacters + 1),
            "wrongKey" => other.ExportPkcs8PrivateKeyPem(),
            "trailingKeyDer" => PemEncoding.WriteString("PRIVATE KEY", [.. key.ExportPkcs8PrivateKey(), 0]),
            "twoKeys" => key.ExportPkcs8PrivateKeyPem() + "\n" + other.ExportPkcs8PrivateKeyPem(),
            "encryptedKey" => key.ExportEncryptedPkcs8PrivateKeyPem("fixture-password", new(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 1000)),
            "certificateInKey" => key.ExportPkcs8PrivateKeyPem() + "\n" + leaf.ExportCertificatePem(),
            _ => key.ExportPkcs8PrivateKeyPem(),
        };
        var exception = Assert.Throws<ArgumentException>(() => TlsClientCertificate.FromPem(certificates, privateKey, Now));
        Assert.Null(exception.InnerException);
        Assert.DoesNotContain("PRIVATE KEY", exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("fixture-password", exception.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("authority")]
    [InlineData("serverOnly")]
    [InlineData("noSignature")]
    [InlineData("expired")]
    [InlineData("future")]
    [InlineData("weakKey")]
    [InlineData("emptySubject")]
    public void InvalidIdentityPurposeAndTimeAreRejected(string scenario)
    {
        using var key = scenario == "weakKey" ? (AsymmetricAlgorithm)RSA.Create(1024) : ECDsa.Create();
        using var leaf = Certificate(key, scenario, scenario == "emptySubject" ? "" : "CN=client");
        Assert.Throws<ArgumentException>(() => TlsClientCertificate.FromPkcs12(leaf.Export(X509ContentType.Pfx), [], Now));
        Assert.Throws<ArgumentException>(() => TlsClientCertificate.FromPem(leaf.ExportCertificatePem(), key.ExportPkcs8PrivateKeyPem(), Now));
    }

    [Fact]
    public void AClientCertificateWithoutOptionalUsageExtensionsCanAuthenticate()
    {
        using var key = ECDsa.Create();
        using var certificate = new CertificateRequest("CN=client", key, HashAlgorithmName.SHA256)
            .CreateSelfSigned(Now.AddDays(-1), Now.AddDays(1));
        using var material = TlsClientCertificate.FromPem(certificate.ExportCertificatePem(), key.ExportPkcs8PrivateKeyPem(), Now);
        Assert.True(material.Context(Now).TargetCertificate.HasPrivateKey);
    }

    [Fact]
    public void ReuseRechecksTimeAndDisposalReleasesPrivateKeyAndCannotBeReused()
    {
        using var key = ECDsa.Create();
        using var leaf = Certificate(key);
        var material = TlsClientCertificate.FromPem(leaf.ExportCertificatePem(), key.ExportPkcs8PrivateKeyPem(), Now);
        var context = material.Context(Now);
        Assert.Throws<ArgumentException>(() => material.Context(Now.AddDays(2)));
        Assert.Throws<ArgumentException>(() => material.ValidateForUse(Now.AddDays(-2)));
        material.Dispose();
        material.Dispose();
        Assert.Equal(IntPtr.Zero, context.TargetCertificate.Handle);
        Assert.Throws<ObjectDisposedException>(() => material.ExportPkcs12());
        Assert.Throws<ObjectDisposedException>(() => material.Context(Now));
        Assert.Throws<ObjectDisposedException>(() => material.ValidateForUse(Now));
    }

    private static X509Certificate2 Certificate(AsymmetricAlgorithm key, string scenario = "valid", string subject = "CN=client")
    {
        var request = key is RSA rsa
            ? new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            : new CertificateRequest(subject, (ECDsa)key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(scenario == "authority", false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(scenario == "noSignature" ? X509KeyUsageFlags.KeyEncipherment : X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(scenario == "serverOnly" ? "1.3.6.1.5.5.7.3.1" : "1.3.6.1.5.5.7.3.2")], false));
        return request.CreateSelfSigned(scenario == "future" ? Now.AddHours(1) : Now.AddDays(-1),
            scenario == "expired" ? Now.AddHours(-1) : Now.AddDays(1));
    }
}
