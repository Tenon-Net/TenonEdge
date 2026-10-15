using TenonEdge.Abstractions.Contracts;
using TenonEdge.Adapters.Http;

namespace TenonEdge.UnitTests.Adapters;

public sealed class HttpClientCertificateIdentityTests
{
    [Theory]
    [InlineData(HttpCredentialKind.Basic, "alice", "c1db33cbde4cb5300dd8a94097d2d5b003970a2580050bb186dd91ec70e176a3")]
    [InlineData(HttpCredentialKind.Bearer, null, "a84c280e309e497aef558dd00db3be297ded54a64256f2c13a2b82f4d82bfe45")]
    [InlineData(HttpCredentialKind.ApiKeyHeader, null, "7769e26f661983d44ab57e52d5a8f48d1539a0fcd70fc6e2e82d15150bf1eef6")]
    [InlineData(HttpCredentialKind.ApiKeyQuery, null, "20e2499c80ec3128421005a76bdd4fea3447c2c7213fc992edb7aec895ded82e")]
    public void ExistingAccountFingerprintsRemainByteCompatible(HttpCredentialKind kind, string? username, string hash)
    {
        var settings = new HttpOutputSettings { ConnectionId = 1, BaseAddress = "https://example.com/api/" };
        var identity = new HttpCredentialIdentity(kind, username, null, "a", "v1");
        Assert.Equal("sha256:" + hash, settings.Fingerprint(identity));
        Assert.Equal(settings.Fingerprint(identity), settings.Fingerprint(identity with { Version = "rotated" }));
    }

    [Fact]
    public void SubjectAndKindSeparateAccountsButCertificateRenewalDoesNot()
    {
        var settings = new HttpOutputSettings { ConnectionId = 1, BaseAddress = "https://example.com/api/" };
        var certificate = new HttpCredentialIdentity(HttpCredentialKind.ClientCertificate, null, null, "a", "v1", "CN=client");
        Assert.Equal(settings.Fingerprint(certificate), settings.Fingerprint(certificate with { Version = "v2" }));
        Assert.NotEqual(settings.Fingerprint(certificate), settings.Fingerprint(certificate with { CertificateSubject = "CN=other" }));
        Assert.NotEqual(settings.Fingerprint(certificate), settings.Fingerprint(certificate with { AccountMarker = "b" }));
        Assert.NotEqual(settings.Fingerprint(certificate), settings.Fingerprint(new(HttpCredentialKind.Basic, "CN=client", null, "a", "v1")));
        var summary = OutputRecipientIdentity.Http(settings.BaseAddress, certificate);
        Assert.Equal(summary, OutputRecipientIdentity.Read(summary.Freeze()));
        Assert.Equal("CN=client", summary.Account);
        Assert.Equal("CN=client", summary.ClientCertificateSubject);
        Assert.Throws<ArgumentException>(() => (summary with { ClientCertificateSubject = null }).Freeze());
    }
}
