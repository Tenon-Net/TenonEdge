using TenonEdge.Abstractions.Contracts;
using TenonEdge.Adapters.Mqtt;

namespace TenonEdge.UnitTests.Adapters;

public sealed class MqttClientCertificateIdentityTests
{
    [Theory]
    [InlineData(null, "ca926bd066ed20403f8a381a4d1d4be67030d49bd2f850e11922b4a9a399871b")]
    [InlineData("alice", "9e7c6625740e7c1ed7dee3192d0f7738969c61ac553a9471750f06e2c79672ce")]
    public void PasswordAndAnonymousFingerprintsRemainCompatible(string? user, string hash)
    {
        var settings = new MqttConnectionSettings { ConnectionId = 1, Host = "example.com", Port = 8883, Tls = true };
        MqttCredentialIdentity? identity = user is null ? null : new(user, "a", "v1");
        Assert.Equal("sha256:" + hash, settings.Fingerprint(identity));
        Assert.Equal(settings.Fingerprint(user, identity?.AccountMarker), settings.Fingerprint(identity));
        if (identity is not null)
        { Assert.Equal(settings.Fingerprint(identity), settings.Fingerprint(identity with { Version = "v2" })); }
    }

    [Fact]
    public void CertificateSubjectAndTypeSeparateAccountsButRenewalDoesNot()
    {
        var settings = new MqttConnectionSettings { ConnectionId = 1, Host = "example.com", Port = 8883, Tls = true };
        var identity = new MqttCredentialIdentity("", "a", "v1", MqttCredentialKind.ClientCertificate, "CN=client");
        Assert.Equal(settings.Fingerprint(identity), settings.Fingerprint(identity with { Version = "v2" }));
        Assert.NotEqual(settings.Fingerprint(identity), settings.Fingerprint(identity with { CertificateSubject = "CN=other" }));
        Assert.NotEqual(settings.Fingerprint(identity), settings.Fingerprint(identity with { AccountMarker = "b" }));
        Assert.NotEqual(settings.Fingerprint(identity), settings.Fingerprint("CN=client", "a"));
        var summary = OutputRecipientIdentity.Mqtt(settings.Recipient, false, identity);
        Assert.Equal("ClientCertificate", summary.Authentication);
        Assert.Equal("CN=client", summary.Account);
        Assert.Equal(summary.Account, summary.ClientCertificateSubject);
        Assert.Equal(summary, OutputRecipientIdentity.Read(summary.Freeze()));
        Assert.Throws<ArgumentException>(() => (summary with { ClientCertificateSubject = null }).Freeze());
        Assert.Throws<ArgumentException>(() => (summary with { Address = "mqtt://example.com:1883" }).Freeze());
    }

    [Theory]
    [InlineData("plain")]
    [InlineData("username")]
    [InlineData("subject")]
    [InlineData("unknownKind")]
    [InlineData("large")]
    [InlineData("passwordSubject")]
    public void MixedOrInvalidCredentialTypesAreRejected(string scenario)
    {
        var credential = new MqttCredential("", "fixture-pfx", "a", "v1", MqttCredentialKind.ClientCertificate, "CN=client");
        credential = scenario switch
        {
            "username" => credential with { Username = "user" },
            "subject" => credential with { CertificateSubject = null },
            "unknownKind" => credential with { Kind = (MqttCredentialKind)42 },
            "large" => credential with { Password = new string('A', 90000) },
            "passwordSubject" => credential with { Kind = MqttCredentialKind.Password, Username = "user" },
            _ => credential,
        };
        Assert.Throws<ArgumentException>(() => MqttCredentialValidation.Validate(credential, scenario != "plain"));
        Assert.DoesNotContain(credential.Password, credential.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InvalidatedIdentityCannotBecomeAnonymousEvenWithOldSnapshot(bool usePassword)
    {
        var settings = new MqttConnectionSettings { ConnectionId = 1, Host = "localhost", UsePassword = usePassword };
        Assert.Throws<ArgumentException>(() => MqttCredentialValidation.Resolve(settings, new Resolver(null, new("user", "a", "v1"))));
    }

    [Theory]
    [InlineData(MqttCredentialKind.Password)]
    [InlineData(MqttCredentialKind.ClientCertificate)]
    public void CurrentCredentialOverridesOldSnapshotAuthentication(MqttCredentialKind kind)
    {
        var credential = new MqttCredential(kind == MqttCredentialKind.Password ? "user" : "", "private-material", "a", "v1", kind,
            kind == MqttCredentialKind.ClientCertificate ? "CN=client" : null);
        var settings = new MqttConnectionSettings { ConnectionId = 1, Host = "localhost", Tls = true, UsePassword = false };
        Assert.Same(credential, MqttCredentialValidation.Resolve(settings, new Resolver(credential, credential.Identity)));
        Assert.DoesNotContain("private-material", credential.ToString(), StringComparison.Ordinal);
        var summary = OutputRecipientIdentity.Mqtt(settings.Recipient, false, credential.Identity);
        Assert.Equal(kind == MqttCredentialKind.Password ? "password" : "ClientCertificate", summary.Authentication);
    }

    private sealed class Resolver(MqttCredential? value, MqttCredentialIdentity? identity) : ICredentialResolver
    {
        public MqttCredential? ResolveMqtt(long connectionId, string recipient) => value;
        public MqttCredentialIdentity? DescribeMqtt(long connectionId) => identity;
    }
}
