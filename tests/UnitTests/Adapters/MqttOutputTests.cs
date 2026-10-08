using System.Text.Json;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Abstractions.Plans;
using TenonEdge.Adapters.Mqtt;

namespace TenonEdge.UnitTests.Adapters;

public sealed class MqttOutputTests
{
    [Fact]
    public void ConnectionIdentityIsIndependentOfOutputAndCanonicalAcrossHostCasing()
    {
        var output = MqttOutputSettings.Parse(Configuration("{}"));
        var connection = new MqttConnectionSettings
        {
            ConnectionId = output.ConnectionId,
            Host = output.Host.ToUpperInvariant(),
            Port = output.Port,
            Tls = output.Tls,
            UsePassword = output.UsePassword,
            TimeoutMs = output.TimeoutMs,
            PingIntervalMs = output.PingIntervalMs,
        };
        Assert.Equal(output.ConnectionIdentity("client"), connection.ConnectionIdentity("client"));
        Assert.Equal(output.Fingerprint("user", "account"), connection.Fingerprint("user", "account"));
        Assert.NotEqual(connection.ConnectionIdentity("client"), (connection with { TimeoutMs = connection.TimeoutMs + 1 }).ConnectionIdentity("client"));
        Assert.Equal(connection.Fingerprint("user", "account"), (connection with { TimeoutMs = connection.TimeoutMs + 1 }).Fingerprint("user", "account"));
        Assert.NotEqual(connection.ConnectionIdentity("client"), connection.ConnectionIdentity("other-client"));
        Assert.Equal(output.ConnectionIdentity("client"), (output with { Topic = "other", Qos = 0 }).ConnectionIdentity("client"));
    }

    [Theory]
    [InlineData("{\"topic\":null}")]
    [InlineData("{\"topic\":\"out/+\"}")]
    [InlineData("{\"topic\":\"out/#\"}")]
    [InlineData("{\"topic\":\"out/{unknown}\"}")]
    [InlineData("{\"topic\":\"\"}")]
    [InlineData("{\"qos\":2}")]
    [InlineData("{\"host\":\"http://broker\"}")]
    [InlineData("{\"host\":\"user:password@broker\"}")]
    [InlineData("{\"connectionId\":0}")]
    [InlineData("{\"connectionId\":9007199254740992}")]
    [InlineData("{\"port\":0}")]
    [InlineData("{\"port\":65536}")]
    [InlineData("{\"timeoutMs\":99}")]
    [InlineData("{\"pingIntervalMs\":999}")]
    [InlineData("{\"clientId\":\" \"}")]
    [InlineData("{\"inputs\":[]}")]
    [InlineData("{\"inputs\":[\"meter/sim\",\"meter/sim\"]}")]
    [InlineData("{\"inputs\":[\"meter\"]}")]
    [InlineData("{\"inputs\":[null]}")]
    [InlineData("{\"password\":\"never-in-snapshot\"}")]
    public void InvalidAndSecretConfigurationIsAStructuralFailure(string patch)
    {
        var config = Configuration(patch);
        var factory = new MqttOutputFactory(new Credentials(), TimeProvider.System, new NoNetwork(), new TenonEdge.Runtime.Connections.SharedConnections());
        Assert.Throws<RuntimeUnitBuildException>(() => factory.Build(Plan(config)));
    }

    [Fact]
    public void PasswordConfigurationRequiresResolvableRecipientBoundCredentials()
    {
        var factory = new MqttOutputFactory(new Credentials(), TimeProvider.System, new NoNetwork(), new TenonEdge.Runtime.Connections.SharedConnections());
        Assert.Throws<RuntimeUnitBuildException>(() => factory.Build(Plan(Configuration("{\"usePassword\":true}"))));
    }

    [Theory]
    [InlineData("BROKER.EXAMPLE", false, "mqtt://broker.example:1883")]
    [InlineData("::1", true, "mqtts://[::1]:1883")]
    [InlineData("[::1]", true, "mqtts://[::1]:1883")]
    [InlineData("0:0:0:0:0:0:0:1", true, "mqtts://[::1]:1883")]
    public void RecipientIsCanonical(string host, bool tls, string expected)
    {
        var settings = MqttOutputSettings.Parse(Configuration(JsonSerializer.Serialize(new { host, tls })));
        Assert.Equal(expected, settings.Recipient);
    }

    [Fact]
    public async Task FormattingFreezesEnvelopeTopicQosAndIdentityWithoutSecretOrNetwork()
    {
        var credentials = new Credentials { Value = new("user", "test-secret", "account", "v1") };
        var factory = new MqttOutputFactory(credentials, TimeProvider.System, new NoNetwork(), new TenonEdge.Runtime.Connections.SharedConnections());
        var target = factory.Build(Plan(Configuration("{\"usePassword\":true,\"host\":\"not-a-real-broker.invalid\"}")));
        var at = DateTimeOffset.UtcNow;
        var message = new EdgeMessage(Guid.NewGuid(), "sim", "meter", at, TimeSource.GatewayUnsynced, at,
            new Dictionary<string, FieldValue> { ["value"] = FieldValue.FromUInt64(ulong.MaxValue) });
        var delivery = Guid.NewGuid();
        var output = target.CreateMessage(message, delivery);
        Assert.Equal("out/meter", output.Address);
        Assert.Equal(1, output.QualityOfService);
        Assert.Equal(StandardEnvelope.Serialize(message, delivery), output.Payload.ToArray());
        Assert.DoesNotContain("test-secret", output.FingerprintSummary, StringComparison.Ordinal);
        Assert.Contains("\"user\"", output.FingerprintSummary, StringComparison.Ordinal);
        var oldFingerprint = output.Fingerprint;
        credentials.Value = credentials.Value with { Password = "rotated", Version = "v2" };
        Assert.Equal(oldFingerprint, target.CreateMessage(message, delivery).Fingerprint);
        credentials.Value = credentials.Value with { AccountMarker = "other-account" };
        Assert.NotEqual(oldFingerprint, target.CreateMessage(message, delivery).Fingerprint);
        // 构建和释放均不连接不存在的地址。
        await using var adapter = target.CreateAdapter();
    }

    [Fact]
    public async Task AnUnavailablePasswordDoesNotPreventFormattingPendingMessagesWithTheirKnownIdentity()
    {
        var credentials = new Credentials { Value = new("user", "secret", "account", "v1") };
        var target = new MqttOutputFactory(credentials, TimeProvider.System, new NoNetwork(), new TenonEdge.Runtime.Connections.SharedConnections()).Build(Plan(Configuration("{\"usePassword\":true}")));
        var at = DateTimeOffset.UtcNow;
        var message = new EdgeMessage(Guid.NewGuid(), "sim", "meter", at, TimeSource.GatewayUnsynced, at,
            new Dictionary<string, FieldValue> { ["value"] = FieldValue.FromInt64(42) });
        var expected = target.CreateMessage(message, Guid.NewGuid()).Fingerprint;
        credentials.Available = false;
        var pending = target.CreateMessage(message, Guid.NewGuid());
        Assert.Equal(expected, pending.Fingerprint);
        await using var adapter = target.CreateAdapter();
        Assert.Equal(OutputSendStatus.SuspendTarget, (await adapter.SendAsync(pending, TestContext.Current.CancellationToken)).Status);
    }

    [Fact]
    public void DefaultClientIdsAreStableForTheGatewayAndDifferentAcrossGatewaysAndConnections()
    {
        const string firstNode = "00000000-0000-4000-8000-000000000001";
        const string otherNode = "00000000-0000-4000-8000-000000000002";
        var first = new MqttOutputFactory(new Credentials(), TimeProvider.System, new NoNetwork(), new TenonEdge.Runtime.Connections.SharedConnections(), firstNode);
        var restarted = new MqttOutputFactory(new Credentials(), TimeProvider.System, new NoNetwork(), new TenonEdge.Runtime.Connections.SharedConnections(), firstNode);
        var other = new MqttOutputFactory(new Credentials(), TimeProvider.System, new NoNetwork(), new TenonEdge.Runtime.Connections.SharedConnections(), otherNode);
        var plan = Plan(Configuration("{}"));
        var client = first.Build(plan).ClientId;
        Assert.Equal(client, restarted.Build(plan).ClientId);
        Assert.NotEqual(client, other.Build(plan).ClientId);
        Assert.NotEqual(client, first.Build(Plan(Configuration("{\"connectionId\":2}"))).ClientId);
        Assert.Equal("explicit", first.Build(Plan(Configuration("{\"clientId\":\"explicit\"}"))).ClientId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueueWaitUsesTotalDeadlineAndCallerCancellationRemainsCancellation(bool cancelCaller)
    {
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var connections = new TenonEdge.Runtime.Connections.SharedConnections();
        var settings = MqttOutputSettings.Parse(Configuration("{}"));
        await using var owner = connections.Acquire(1, "mqtt", "test", () => new MqttSession(settings, "deadline-test", new Credentials(), time, new NoNetwork()));
        var borrower = connections.Acquire<MqttSession>(1, "mqtt", "test", () => throw new InvalidOperationException("must share"));
        await using var adapter = new MqttOutputAdapter(borrower, time, 1000);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holding = owner.ExecuteAsync(async (_, _) => { await release.Task; return true; }, TestContext.Current.CancellationToken);
        try
        {
            using var caller = new CancellationTokenSource();
            var waiting = adapter.SendAsync(new OutputMessage(ReadOnlyMemory<byte>.Empty, "out", 1, "unused", "unused"), caller.Token);
            Assert.False(waiting.IsCompleted);
            if (cancelCaller)
            {
                await caller.CancelAsync();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
            }
            else
            {
                time.Advance(TimeSpan.FromMilliseconds(999));
                Assert.False(waiting.IsCompleted);
                time.Advance(TimeSpan.FromMilliseconds(1));
                var result = await waiting.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
                Assert.Equal(OutputSendStatus.Retry, result.Status);
                Assert.False(result.CountsTowardPoison);
            }
            Assert.False(holding.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
            await holding;
        }
    }

    private static RuntimeUnitPlan Plan(JsonElement config) => new(RuntimeUnitId.Create("output.mqtt", "mqtt-out"), config);

    private static JsonElement Configuration(string patch)
    {
        var values = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""{"connectionId":1,"host":"127.0.0.1","topic":"out/{deviceKey}","inputs":["meter/sim"]}""")!;
        foreach (var item in JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(patch)!)
        {
            values[item.Key] = item.Value;
        }

        return JsonSerializer.SerializeToElement(values);
    }

    private sealed class NoNetwork : IOutboundConnector
    {
        public ValueTask<Stream> ConnectAsync(string host, int port, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("此单元测试不允许网络连接。");
    }

    private sealed class Credentials : ICredentialResolver
    {
        public MqttCredential? Value { get; set; }
        public bool Available { get; set; } = true;
        public MqttCredentialIdentity? DescribeMqtt(long connectionId) => Value is { } value ? new(value.Username, value.AccountMarker, value.Version) : null;
        public MqttCredential? ResolveMqtt(long connectionId, string recipient) => Available ? Value : null;
    }
}
