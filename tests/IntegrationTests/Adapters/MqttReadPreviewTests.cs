using System.Net;
using System.Text;
using System.Text.Json;
using MQTTnet;
using MQTTnet.Protocol;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Adapters.Mqtt;
using TenonEdge.IntegrationTests.Probes.Mqtt;
using TenonEdge.Runtime.Connections;
using TenonEdge.Runtime.Diagnostics;
using TenonEdge.Runtime.Mapping;

namespace TenonEdge.IntegrationTests.Adapters;

[Trait("Category", "RealBroker")]
public sealed class MqttReadPreviewTests(MosquittoBrokerFixture fixture) : IClassFixture<MosquittoBrokerFixture>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static MqttCredential Credential => new(MosquittoBroker.UserName, MosquittoBroker.Password, "fixture", "1");

    [Fact]
    public async Task RetainedSamplePreservesExactNumbersWithoutDisplacingOrAcknowledgingRuntimeMessages()
    {
        var broker = fixture.Require();
        var topic = MqttProbe.NewTopic("sample-isolation");
        var runtimeId = "runtime-" + Guid.NewGuid().ToString("N");
        var (runtime, _) = await MqttProbe.ConnectAsync(MqttProbe.Options(broker, runtimeId, cleanSession: false), Token);
        using (runtime)
        {
            var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            runtime.ApplicationMessageReceivedAsync += args =>
            {
                args.AutoAcknowledge = false;
                received.TrySetResult();
                return Task.CompletedTask;
            };
            await MqttProbe.SubscribeAsync(runtime, topic, Token);
            await PublishRetained(broker, topic, "{\"value\":9223372036854775807}");
            await received.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            var budget = Budget();
            var result = await Probe(budget: budget).RunAsync(Settings(broker, topic) with { ClientId = runtimeId }, ClockSyncState.Unknown, Token);
            Assert.Equal("success", result.Code);
            Assert.Equal(topic, result.Topic);
            Assert.True(result.Retained);
            Assert.Equal(1, result.Qos);
            Assert.True(result.Preview!.CanUseAsSample);
            Assert.Equal("9223372036854775807", Assert.Single(result.Preview.Fields).Text);
            Assert.True(result.Preview.Fields[0].UnsafeJavaScriptInteger);
            Assert.Equal(0, budget.Status.ReservedBytes);
            await runtime.PingAsync(Token);
            Assert.True(runtime.IsConnected);
        }
        var replay = new TaskCompletionSource<MqttApplicationMessageReceivedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var resumed = new MqttClientFactory().CreateMqttClient();
        resumed.ApplicationMessageReceivedAsync += args => { args.AutoAcknowledge = false; replay.TrySetResult(args); return Task.CompletedTask; };
        var connected = await resumed.ConnectAsync(MqttProbe.Options(broker, runtimeId, cleanSession: false), Token);
        Assert.True(connected.IsSessionPresent);
        // 未重新订阅仍收到未确认消息，诊断 PUBACK 没有确认正式会话的消息。
        var message = await replay.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.True(message.ApplicationMessage.Dup);
        Assert.Equal(topic, message.ApplicationMessage.Topic);
    }

    [Theory]
    [InlineData("{\"password\":\"private\",\"value\":42}", "payload.redacted")]
    [InlineData("not-json", "payload.invalidJson")]
    [InlineData("{\"value\":1} trailing", "payload.invalidJson")]
    public async Task UnsafeOrInvalidPayloadsDoNotReturnRawSamples(string payload, string expected)
    {
        var broker = fixture.Require();
        var topic = MqttProbe.NewTopic("unsafe-sample");
        await PublishRetained(broker, topic, payload);
        var result = await Probe().RunAsync(Settings(broker, topic), ClockSyncState.Unknown, Token);
        Assert.Equal(expected, result.Code);
        Assert.True(result.Preview is null || !result.Preview.CanUseAsSample);
        Assert.Null(result.Preview?.RawJson);
    }

    [Fact]
    public async Task CredentialEchoIsHiddenAndDisplayIsBounded()
    {
        var broker = fixture.Require();
        var topic = MqttProbe.NewTopic("credential-echo");
        await PublishRetained(broker, topic, JsonSerializer.Serialize(new { echo = Credential.Password, value = 42 }));
        var hidden = await Probe().RunAsync(Settings(broker, topic), ClockSyncState.Unknown, Token);
        Assert.Equal("payload.redacted", hidden.Code);
        Assert.Null(hidden.Preview!.RawJson);
        await PublishRetained(broker, topic, JsonSerializer.Serialize(new { value = new string('中', 60000) }));
        var result = await Probe().RunAsync(Settings(broker, topic), ClockSyncState.Unknown, Token);
        Assert.True(result.Preview!.RawTruncated);
        Assert.False(result.Preview.CanUseAsSample);
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(result, JsonSerializerOptions.Web).Length <= 65536);
    }

    [Fact]
    public async Task TimeoutAndCancellationReleaseRealClientAndInputBudget()
    {
        var broker = fixture.Require();
        var budget = Budget();
        var probe = Probe(budget: budget);
        var settings = Settings(broker, MqttProbe.NewTopic("no-next-message"));
        var result = await probe.RunAsync(settings, ClockSyncState.Unknown, Token, timeoutMs: 300);
        Assert.Equal("timeout", result.Code);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        stop.CancelAfter(300);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe.RunAsync(settings, ClockSyncState.Unknown, stop.Token));
        Assert.Equal(0, budget.Status.ReservedBytes);
    }

    [Fact]
    public async Task MissingCredentialAndInvalidFilterNeverConnect()
    {
        var settings = Settings(fixture.Require(), "valid/topic");
        var probe = Probe(connector: new ForbiddenConnector(), credentials: new Resolver(null));
        Assert.Equal("credential.unavailable", (await probe.RunAsync(settings, ClockSyncState.Unknown, Token)).Code);
        await Assert.ThrowsAsync<ArgumentException>(() => probe.RunAsync(settings with { TopicFilter = "invalid/#/tail" }, ClockSyncState.Unknown, Token));
    }

    [Fact]
    public async Task TlsAndBrokerCredentialRejectionsReturnSafeCodes()
    {
        var broker = fixture.Require();
        var settings = Settings(broker, MqttProbe.NewTopic("denied"));
        Assert.Equal("credential.rejected", (await Probe(credentials: new Resolver(Credential with { Password = "wrong-fixture" }))
            .RunAsync(settings, ClockSyncState.Unknown, Token)).Code);
        Assert.Equal("certificate.invalid", (await Probe().RunAsync(settings with { Host = "localhost", Port = broker.TlsPort, Tls = true }, ClockSyncState.Unknown, Token)).Code);
    }

    private static async Task PublishRetained(MosquittoBroker broker, string topic, string payload)
    {
        var (client, _) = await MqttProbe.ConnectAsync(MqttProbe.Options(broker, "publisher-" + Guid.NewGuid().ToString("N")), Token);
        using (client)
        {
            var result = await client.PublishAsync(new MqttApplicationMessageBuilder().WithTopic(topic)
                .WithPayload(Encoding.UTF8.GetBytes(payload)).WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).WithRetainFlag().Build(), Token);
            Assert.True(result.IsSuccess);
        }
    }

    private static MqttInputSettings Settings(MosquittoBroker broker, string topic) => new()
    {
        ConnectionId = 1,
        Host = "127.0.0.1",
        Port = broker.Port,
        UsePassword = true,
        TopicFilter = topic,
        Fields = JsonSerializer.SerializeToElement(new[] { new { key = "value", path = "/value", type = "integer" } })
    };
    private static InFlightBudget Budget() => new(128 * 1024 * 1024, 64 * 1024 * 1024);
    private static MqttReadPreview Probe(IInputBufferBudget? budget = null, IOutboundConnector? connector = null, ICredentialResolver? credentials = null) =>
        new(new SharedConnections(), connector ?? new OutboundConnector(new Policy()), credentials ?? new Resolver(Credential), new JsonInputDecoderFactory(), budget ?? Budget(), TimeProvider.System);
    private sealed class Policy : IOutboundAddressPolicy { public bool IsAllowed(IPAddress address, int port) => IPAddress.IsLoopback(address); }
    private sealed class Resolver(MqttCredential? credential) : ICredentialResolver
    {
        public MqttCredentialIdentity? DescribeMqtt(long connectionId) => credential is null ? null : new(credential.Username, credential.AccountMarker, credential.Version);
        public MqttCredential? ResolveMqtt(long connectionId, string recipient) => credential;
    }
    private sealed class ForbiddenConnector : IOutboundConnector
    {
        public ValueTask<Stream> ConnectAsync(string host, int port, CancellationToken cancellationToken) => throw new InvalidOperationException("must not connect");
    }
}
