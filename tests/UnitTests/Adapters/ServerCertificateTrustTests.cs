using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Adapters.Diagnostics;

namespace TenonEdge.UnitTests.Adapters;

public sealed class ServerCertificateTrustTests
{
    [Theory]
    [InlineData("empty")]
    [InlineData("base64")]
    [InlineData("oversize")]
    [InlineData("unknownMode")]
    [InlineData("pfx")]
    [InlineData("trailing")]
    [InlineData("leafAsCa")]
    [InlineData("noVersion")]
    public void MalformedTrustCannotFallBackToSystemTrust(string scenario)
    {
        using var key = ECDsa.Create();
        using var certificate = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var trust = new TlsTrustSnapshot(TlsTrustMode.PinnedCertificate, Convert.ToBase64String(certificate.RawData), "1");
        trust = scenario switch
        {
            "empty" => trust with { CertificateDerBase64 = "" },
            "base64" => trust with { CertificateDerBase64 = "not base64" },
            "oversize" => trust with { CertificateDerBase64 = new string('A', 45000) },
            "unknownMode" => trust with { Mode = (TlsTrustMode)99 },
            "pfx" => trust with { CertificateDerBase64 = Convert.ToBase64String(certificate.Export(X509ContentType.Pfx)) },
            "trailing" => trust with { CertificateDerBase64 = Convert.ToBase64String([.. certificate.RawData, 0]) },
            "leafAsCa" => trust with { Mode = TlsTrustMode.CertificateAuthority },
            _ => trust with { Version = "" },
        };
        Assert.Equal("certificate.trustInvalid", ServerCertificateTrust.Evaluate(certificate, null, SslPolicyErrors.None, trust));
    }

    [Fact]
    public void PinIsCheckedEvenIfPlatformWouldTrustTheReplacement()
    {
        using var key = ECDsa.Create();
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256);
        using var first = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        using var second = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var trust = new TlsTrustSnapshot(TlsTrustMode.PinnedCertificate, Convert.ToBase64String(first.RawData), "1");
        Assert.Equal("certificate.changed", ServerCertificateTrust.Evaluate(second, null, SslPolicyErrors.None, trust));
        Assert.Equal("certificate.missing", ServerCertificateTrust.Evaluate(null, null, SslPolicyErrors.None, trust));
        Assert.Equal("certificate.nameMismatch", ServerCertificateTrust.Evaluate(first, null, SslPolicyErrors.RemoteCertificateNameMismatch, trust));
        Assert.Null(ServerCertificateTrust.Evaluate(second, null, SslPolicyErrors.None, null));
    }

    [Fact]
    public void OfflineChainPolicyDoesNotDownloadCertificatesOrCheckRevocationOnline()
    {
        var policy = ServerCertificateTrust.OfflineChainPolicy();
        Assert.True(policy.DisableCertificateDownloads);
        Assert.Equal(X509RevocationMode.NoCheck, policy.RevocationMode);
        Assert.Equal(X509VerificationFlags.NoFlag, policy.VerificationFlags);
        Assert.Equal("1.3.6.1.5.5.7.3.1", Assert.Single(policy.ApplicationPolicy.Cast<Oid>()).Value);
    }
}
