using TenonEdge.Host.Features.Configuration;
using TenonEdge.Host.Features.Configuration.Transfer;
using TenonEdge.Host.Features.Connections;

namespace TenonEdge.UnitTests.Configuration;

public sealed class ConfigurationImportCredentialTests
{
    private static readonly string Identity = ConfigurationCredentialMetadata.AccountDigest("http", null, 1, "fixture-account-marker");
    private static ConnectionDraft Connection(string address = "https://example.com/base/") => new(123, "target", "Target", ConnectionKind.Http, true, null, null, new(address));
    private static ConfigurationCheckpoint Draft(ConnectionDraft connection) => new("version", [connection], [], [], []);
    private static PortableConfiguration File(string? identity = null, bool anonymous = false) => new(1,
        [new("target", "Target", ConnectionKind.Http, true, null, null, new("https://example.com/base/"), anonymous ? null : new("requiresInput", identity ?? Identity))], [], [], []);
    private static ConfigurationCredentialMetadata Metadata(string address = "https://example.com/base/", bool hasSecret = true) => new(123, ConnectionKind.Http, address, "credential-version", Identity, hasSecret);
    private static ConfigurationImportCredentialDecision Compare(PortableConfiguration? file = null, ConnectionDraft? before = null,
        ConnectionDraft? after = null, ConfigurationCredentialMetadata? metadata = null, bool required = false) =>
        Assert.Single(ConfigurationImportCredentials.Compare(file ?? File(), Draft(before ?? Connection()), Draft(after ?? Connection()),
            [metadata ?? Metadata()], required ? new HashSet<long> { 123 } : new HashSet<long>()));

    [Fact]
    public void OnlySameDraftRecipientActualCredentialBindingAndAccountCanBePreserved()
    {
        var decision = Compare();
        Assert.Equal("preserved", decision.Action);
        Assert.Equal("sameRecipientAndAccount", decision.Reason);
        Assert.Equal(123, decision.ConnectionId);
        Assert.Equal("target", decision.ConnectionKey);
        Assert.Equal("preserved", Compare(metadata: Metadata() with { Version = "rotated-secret-version" }).Action);
    }

    [Theory]
    [InlineData("http://example.com/base/")]
    [InlineData("https://other.example.com/base/")]
    [InlineData("https://example.com:8443/base/")]
    [InlineData("https://example.com/other/")]
    public void SchemeHostPortOrBasePathChangesRequireRefill(string target)
    {
        var decision = Compare(after: Connection(target));
        Assert.Equal("requiresInput", decision.Action);
        Assert.Equal("recipientChanged", decision.Reason);
    }

    [Fact]
    public void ComparingOnlyTheTwoDraftAddressesCannotPreserveACredentialBoundElsewhere()
    {
        var decision = Compare(before: Connection("https://new.example.com/"), after: Connection("https://new.example.com/"));
        Assert.Equal("requiresInput", decision.Action);
        Assert.Equal("recipientChanged", decision.Reason);
    }

    [Fact]
    public void AccountChangesAndAnAnonymousImportedFileCannotSilentlyRetainAnExistingSecret()
    {
        Assert.Equal("accountChanged", Compare(file: File("sha256:" + new string('f', 64))).Reason);
        Assert.Equal("accountChanged", Compare(file: File(anonymous: true)).Reason);
        Assert.Equal("credentialUnavailable", Compare(metadata: Metadata(hasSecret: false)).Reason);
        Assert.Equal("alreadyRequiresInput", Compare(required: true).Reason);
    }

    [Fact]
    public void NewConnectionsRequireRefillOnlyIfTheExportDeclaredACredential()
    {
        var current = Draft(Connection());
        Assert.Equal("requiresInput", Assert.Single(ConfigurationImportCredentials.Compare(File(), current, current, [], new HashSet<long>())).Action);
        Assert.Equal("none", Assert.Single(ConfigurationImportCredentials.Compare(File(anonymous: true), current, current, [], new HashSet<long>())).Action);
    }

    [Fact]
    public void AccountDigestDependsOnProtocolAccountAndAuthenticationKindNotOnASecretOrItsVersion()
    {
        Assert.Matches("^sha256:[a-f0-9]{64}$", Identity);
        Assert.Equal(Identity, ConfigurationCredentialMetadata.AccountDigest("http", null, 1, "fixture-account-marker"));
        Assert.NotEqual(Identity, ConfigurationCredentialMetadata.AccountDigest("mqtt", null, 1, "fixture-account-marker"));
        Assert.NotEqual(Identity, ConfigurationCredentialMetadata.AccountDigest("http", "different", 1, "fixture-account-marker"));
        Assert.NotEqual(Identity, ConfigurationCredentialMetadata.AccountDigest("http", null, 2, "fixture-account-marker"));
        Assert.NotEqual(Identity, ConfigurationCredentialMetadata.AccountDigest("http", null, 1, "different-marker"));
        Assert.DoesNotContain("fixture-account-marker", Identity, StringComparison.Ordinal);
    }
}
