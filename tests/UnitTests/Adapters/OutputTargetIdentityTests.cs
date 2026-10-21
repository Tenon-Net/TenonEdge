using System.Text.Json;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Abstractions.Plans;
using TenonEdge.Adapters.Http;
using TenonEdge.Adapters.Mqtt;
using TenonEdge.Runtime.Connections;

namespace TenonEdge.UnitTests.Adapters;

public sealed class OutputTargetIdentityTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void CaptureAndFormattingUseOneNonSecretCredentialReadForFingerprintAndAccount(bool mqtt, bool formatting)
    {
        var credentials = new ChangingCredentials();
        var target = Target(mqtt, credentials);
        credentials.Descriptions = 0;
        credentials.AllowSecrets = false; // Factory preparation resolves credentials. Capture/format must not.
        credentials.ChangeAfterDescribe = true;
        string fingerprint, summary;
        if (formatting)
        {
            var output = target.CreateMessage(Message(), Guid.NewGuid());
            (fingerprint, summary) = (output.Fingerprint, output.FingerprintSummary);
        }
        else
        {
            var captured = Assert.IsType<OutputTargetIdentity>(target.CaptureIdentity());
            (fingerprint, summary) = (captured.Fingerprint, captured.Summary);
        }
        Assert.Equal(1, credentials.Descriptions);
        Assert.Equal("new-user", credentials.Username); // The resolver changed immediately after its first answer.
        var frozen = Assert.IsType<OutputRecipientIdentity>(OutputRecipientIdentity.Read(summary));
        Assert.Equal("old-user", frozen.Account);
        Assert.Equal(mqtt ? "mqtt" : "http", frozen.Protocol);
        Assert.Equal(mqtt ? "mqtts://broker.test:8883" : "https://server.test/api/", frozen.Address);
        Assert.Equal(ExpectedFingerprint(mqtt, "old-user", "old-marker"), fingerprint);
        Assert.NotEqual(target.CurrentFingerprint, fingerprint);
        Assert.DoesNotContain("fixture-secret", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("old-marker", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("fixture-version", summary, StringComparison.Ordinal);
        Assert.True(frozen.ClientCertificateRecorded);
        Assert.Null(frozen.ClientCertificateSubject);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CredentialRotationKeepsIdentityButAccountChangeDoesNotRewriteAnAlreadyFrozenMessage(bool mqtt)
    {
        var credentials = new ChangingCredentials();
        var target = Target(mqtt, credentials);
        var original = target.CreateMessage(Message(), Guid.NewGuid());
        credentials.Version = "rotated";
        credentials.Secret = "rotated-secret";
        var rotated = Assert.IsType<OutputTargetIdentity>(target.CaptureIdentity());
        Assert.Equal(original.Fingerprint, rotated.Fingerprint);
        Assert.Equal(original.FingerprintSummary, rotated.Summary);
        credentials.Username = "new-user";
        credentials.Marker = "new-marker";
        var changed = Assert.IsType<OutputTargetIdentity>(target.CaptureIdentity());
        Assert.NotEqual(original.Fingerprint, changed.Fingerprint);
        Assert.Equal("new-user", changed.Recipient.Account);
        Assert.Equal("old-user", OutputRecipientIdentity.Read(original.FingerprintSummary)?.Account);
    }

    [Fact]
    public void MqttMissingDescriptionIsUnknownNotAnInventedEmptyUsername()
    {
        var credentials = new ChangingCredentials();
        var target = Target(true, credentials);
        credentials.Unavailable = true;
        var captured = Assert.IsType<OutputTargetIdentity>(target.CaptureIdentity());
        Assert.False(captured.Recipient.AccountRecorded);
        Assert.Null(captured.Recipient.Account);
        Assert.Equal("password", captured.Recipient.Authentication);
        Assert.Equal(captured.Recipient, OutputRecipientIdentity.Read(captured.Summary));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SummaryRetainedTextAndSerialEncodingWorkspaceAreBothReserved(bool mqtt)
    {
        var target = Target(mqtt, new ChangingCredentials());
        Assert.True(target.FormattingReservationBytes >= 2L * StandardEnvelope.MaximumPayloadBytes + 2L * OutputRecipientIdentity.MaximumSummaryBytes);
        Assert.True(target.MinimumSerializationReservationBytes >= 4L * OutputRecipientIdentity.MaximumSummaryBytes);
        var longestAccount = new string('字', 256);
        var identity = mqtt ? OutputRecipientIdentity.Mqtt("mqtts://broker.test:8883", true, new(longestAccount, "marker", "v"))
            : OutputRecipientIdentity.Http("https://server.test/" + new string('a', 2000), new(HttpCredentialKind.Basic, longestAccount, null, "marker", "v"));
        Assert.Equal(identity, OutputRecipientIdentity.Read(identity.Freeze()));
    }

    private static IOutputTarget Target(bool mqtt, ChangingCredentials credentials)
    {
        IOutputAdapterFactory factory = mqtt
            ? new MqttOutputFactory(credentials, TimeProvider.System, new NoNetwork(), new SharedConnections())
            : new HttpOutputFactory(credentials, TimeProvider.System, new NoNetwork(), new SharedConnections());
        return factory.Build(new(RuntimeUnitId.Create(factory.Kind, "out"), Settings(mqtt)));
    }

    private static string ExpectedFingerprint(bool mqtt, string username, string marker) => mqtt
        ? MqttOutputSettings.Parse(Settings(true)).Fingerprint(username, marker)
        : HttpOutputSettings.Parse(Settings(false)).Fingerprint(new(HttpCredentialKind.Basic, username, null, marker, "fixture-version"));

    private static JsonElement Settings(bool mqtt) => JsonSerializer.Deserialize<JsonElement>(mqtt
        ? """{"connectionId":1,"host":"broker.test","port":8883,"tls":true,"usePassword":true,"topic":"out","inputs":["meter/sample"]}"""
        : """{"connectionId":1,"baseAddress":"https://server.test/api/","path":"send","inputs":["meter/sample"]}""");

    private static EdgeMessage Message()
    {
        var at = DateTimeOffset.UtcNow;
        return new(Guid.NewGuid(), "sample", "meter", at, TimeSource.GatewayUnsynced,
            at, new Dictionary<string, FieldValue> { ["value"] = FieldValue.FromInt64(42) });
    }

    private sealed class NoNetwork : IOutboundConnector
    {
        public ValueTask<Stream> ConnectAsync(string host, int port, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Identity capture cannot open a connection.");
    }

    private sealed class ChangingCredentials : ICredentialResolver, IHttpCredentialResolver
    {
        public int Descriptions;
        public bool AllowSecrets = true;
        public bool ChangeAfterDescribe;
        public bool Unavailable;
        public string Username = "old-user", Marker = "old-marker", Version = "fixture-version", Secret = "fixture-secret";
        public MqttCredentialIdentity? DescribeMqtt(long connectionId)
        {
            Descriptions++;
            var identity = Unavailable ? null : new MqttCredentialIdentity(Username, Marker, Version);
            Change();
            return identity;
        }
        public HttpCredentialIdentity? DescribeHttp(long connectionId)
        {
            Descriptions++;
            var identity = Unavailable ? null : new HttpCredentialIdentity(HttpCredentialKind.Basic, Username, null, Marker, Version);
            Change();
            return identity;
        }
        private void Change()
        {
            if (ChangeAfterDescribe)
            { Username = "new-user"; Marker = "new-marker"; }
        }
        public MqttCredential? ResolveMqtt(long connectionId, string recipient) => AllowSecrets
            ? new(Username, Secret, Marker, Version) : throw new InvalidOperationException("Capture must not resolve secrets.");
        public HttpCredential? ResolveHttp(long connectionId, string recipient) => AllowSecrets
            ? new(new(HttpCredentialKind.Basic, Username, null, Marker, Version), Secret)
            : throw new InvalidOperationException("Capture must not resolve secrets.");
    }
}
