using System.Text.Json;
using TenonEdge.Abstractions.Contracts;

namespace TenonEdge.UnitTests.Abstractions;

public sealed class OutputRecipientIdentityTests
{
    private static OutputRecipientIdentity Known => OutputRecipientIdentity.Http("https://example.test/api/",
        new(HttpCredentialKind.Basic, "工厂; account=\"old\"\n\\", null, "private-marker", "private-version"));

    [Fact]
    public void VersionedSummaryRoundTripsEscapingAndExplicitIdentityKnowledgeWithoutPrivateMetadata()
    {
        var identity = Known;
        var frozen = identity.Freeze();
        Assert.Equal(identity, OutputRecipientIdentity.Read(frozen));
        Assert.StartsWith("recipient:v1:", frozen);
        Assert.DoesNotContain("private-marker", frozen, StringComparison.Ordinal);
        Assert.DoesNotContain("private-version", frozen, StringComparison.Ordinal);
        Assert.True(identity.AccountRecorded);
        Assert.True(identity.ClientCertificateRecorded);
        Assert.Null(identity.ClientCertificateSubject);
    }

    [Theory]
    [InlineData("none", null)]
    [InlineData("Basic", HttpCredentialKind.Basic)]
    [InlineData("Bearer", HttpCredentialKind.Bearer)]
    [InlineData("ApiKeyHeader", HttpCredentialKind.ApiKeyHeader)]
    [InlineData("ApiKeyQuery", HttpCredentialKind.ApiKeyQuery)]
    public void HttpAccountMeansVisibleUsernameNotTokenOrApiKeyName(string authentication, HttpCredentialKind? kind)
    {
        var identity = OutputRecipientIdentity.Http("http://example.test/", kind is { } value
            ? new(value, value == HttpCredentialKind.Basic ? "user" : null, "X-private-key-name", "marker", "version") : null);
        Assert.Equal(authentication, identity.Authentication);
        Assert.True(identity.AccountRecorded);
        Assert.Equal(kind == HttpCredentialKind.Basic ? "user" : null, identity.Account);
        Assert.Equal(identity, OutputRecipientIdentity.Read(identity.Freeze()));
        Assert.DoesNotContain("X-private-key-name", identity.Freeze(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, false, null, true)]
    [InlineData(true, false, null, false)]
    [InlineData(true, true, "user", true)]
    [InlineData(true, true, "", true)]
    public void MqttAbsentIdentityIsNotMisrepresentedAsAnonymous(bool password, bool available, string? username, bool recorded)
    {
        var identity = OutputRecipientIdentity.Mqtt("mqtts://[::1]:8883", password, available ? new(username!, "marker", "version") : null);
        Assert.Equal(password ? "password" : "anonymous", identity.Authentication);
        Assert.Equal(recorded, identity.AccountRecorded);
        Assert.Equal(username, identity.Account);
        Assert.Equal(identity, OutputRecipientIdentity.Read(identity.Freeze()));
    }

    [Theory]
    [InlineData("none", true)]
    [InlineData("Basic", false)]
    [InlineData("Bearer", true)]
    [InlineData("ApiKeyHeader", true)]
    [InlineData("ApiKeyQuery", true)]
    public void LegacyHttpReadsOriginalAddressButNeverFillsMissingUsernameOrCertificate(string kind, bool accountRecorded)
    {
        var identity = Assert.IsType<OutputRecipientIdentity>(OutputRecipientIdentity.Read($"https://old.test/api/; authentication={kind}"));
        Assert.Equal("http", identity.Protocol);
        Assert.Equal("https://old.test/api/", identity.Address);
        Assert.Equal(kind, identity.Authentication);
        Assert.Equal(accountRecorded, identity.AccountRecorded);
        Assert.Null(identity.Account);
        Assert.False(identity.ClientCertificateRecorded);
        Assert.Null(identity.ClientCertificateSubject);
        Assert.Equal(identity, OutputRecipientIdentity.Read(identity.Freeze()));
    }

    [Fact]
    public void LegacyHttpBasePathCanContainSemicolonsAndAuthenticationLikeText()
    {
        const string address = "https://old.test/api/;authentication=Bearer/";
        Assert.Equal(address, OutputRecipientIdentity.Read(address + "; authentication=Basic")?.Address);
    }

    [Theory]
    [InlineData("mqtt://old.test:1883", "password", "account; authentication=anonymous", true)]
    [InlineData("mqtts://[::1]:8883", "password", "\"工厂\\\n", true)]
    [InlineData("mqtt://old.test:1883", "password", "", false)]
    [InlineData("mqtt://old.test:1883", "anonymous", "", true)]
    public void LegacyMqttHandlesJsonAccountWithoutSplittingItsContents(string address, string kind, string username, bool recorded)
    {
        var identity = Assert.IsType<OutputRecipientIdentity>(OutputRecipientIdentity.Read($"{address}; authentication={kind}; account={JsonSerializer.Serialize(username)}"));
        Assert.Equal("mqtt", identity.Protocol);
        Assert.Equal(address, identity.Address);
        Assert.Equal(username.Length == 0 ? null : username, identity.Account);
        Assert.Equal(recorded, identity.AccountRecorded);
        Assert.False(identity.ClientCertificateRecorded);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("test")]
    [InlineData("http://old.test/")]
    [InlineData("http://old.test/; authentication=other")]
    [InlineData("http://old.test/; authentication=Basic; account=\"invented\"")]
    [InlineData("http://user:secret@old.test/; authentication=Basic")]
    [InlineData("http://old.test/?secret=1; authentication=Basic")]
    [InlineData("http://old.test/#secret; authentication=Basic")]
    [InlineData("mqtt://old.test; authentication=password; account=\"user\"")]
    [InlineData("mqtt://old.test:1883/extra; authentication=password; account=\"user\"")]
    [InlineData("mqtt://old.test:1883; authentication=password; account=null")]
    [InlineData("mqtt://old.test:1883; authentication=password; account=\"user\" trailing")]
    [InlineData("mqtt://old.test:1883; authentication=anonymous; account=\"user\"")]
    [InlineData("mqtt://old.test:1883; authentication=password")]
    [InlineData("recipient:v1:null")]
    [InlineData("recipient:v1:[]")]
    [InlineData("recipient:v1:{}")]
    [InlineData("recipient:v1:{broken")]
    public void UnknownOrAmbiguousFrozenIdentityCannotBeUsedAsProtocolEvidence(string? summary) =>
        Assert.Null(OutputRecipientIdentity.Read(summary));

    [Theory]
    [InlineData("version")]
    [InlineData("duplicate")]
    [InlineData("case")]
    [InlineData("missing")]
    [InlineData("secret")]
    [InlineData("protocol")]
    [InlineData("falseKnowledge")]
    [InlineData("nullAddress")]
    public void StructuredSummaryRequiresExactKnownSchemaAndConsistentIdentity(string corruption)
    {
        var summary = Known.Freeze();
        summary = corruption switch
        {
            "version" => summary.Replace("recipient:v1:", "recipient:v2:", StringComparison.Ordinal),
            "duplicate" => summary.Replace("{", "{\"protocol\":\"mqtt\",", StringComparison.Ordinal),
            "case" => summary.Replace("\"protocol\"", "\"Protocol\"", StringComparison.Ordinal),
            "missing" => summary.Replace("\"clientCertificateRecorded\":true", "\"unused\":true", StringComparison.Ordinal),
            "secret" => summary.Replace("{", "{\"password\":\"never-accepted\",", StringComparison.Ordinal),
            "protocol" => summary.Replace("\"protocol\":\"http\"", "\"protocol\":\"mqtt\"", StringComparison.Ordinal),
            "falseKnowledge" => summary.Replace("\"accountRecorded\":true", "\"accountRecorded\":false", StringComparison.Ordinal),
            "nullAddress" => summary.Replace("\"https://example.test/api/\"", "null", StringComparison.Ordinal),
            _ => throw new ArgumentException("test"),
        };
        Assert.Null(OutputRecipientIdentity.Read(summary));
    }

    [Fact]
    public void SummaryIsBoundedOnBothWriteAndReadIncludingUtf8Bytes()
    {
        var oversized = Known with { Account = new string('字', OutputRecipientIdentity.MaximumSummaryBytes) };
        Assert.Throws<ArgumentException>(() => oversized.Freeze());
        Assert.Null(OutputRecipientIdentity.Read(new string('a', OutputRecipientIdentity.MaximumSummaryBytes + 1)));
        Assert.Null(OutputRecipientIdentity.Read(new string('字', OutputRecipientIdentity.MaximumSummaryBytes / 2)));
    }
}
