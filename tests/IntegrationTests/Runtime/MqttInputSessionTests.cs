using System.Collections.Concurrent;
using System.Net;
using MQTTnet;
using MQTTnet.Protocol;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Adapters.Mqtt;
using TenonEdge.IntegrationTests.Probes.Mqtt;
using TenonEdge.Runtime.Connections;

namespace TenonEdge.IntegrationTests.Runtime;

[Trait("Category", "RealBroker")]
public sealed class MqttInputSessionTests(MosquittoBrokerFixture fixture) : IClassFixture<MosquittoBrokerFixture>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task OneSessionSubscribesAndPublishesWithoutAnotherClientOrCallbackDeadlock()
    {
        var broker = fixture.Require();
        var topic = MqttProbe.NewTopic("shared-input-output");
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var acknowledged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var input = new MqttInputReceiver([new(topic, 1, (message, _) =>
        {
            received.TrySetResult(message.ConvertPayloadToString());
            return Task.FromResult(InputHandoffStatus.Committed);
        })], TimeProvider.System, reason => { if (reason == "acknowledged") { acknowledged.TrySetResult(); } });
        var connector = new CountingConnector();
        var settings = Settings(broker);
        var credentials = new Credentials();
        await using var session = new MqttSession(settings, NewId(), credentials, TimeProvider.System, connector, input);
        using var deadline = MqttProbe.Timeout(Token);
        Assert.Equal(OutputSendStatus.Ack, (await session.MaintainAsync(deadline.Token)).Status);
        Assert.False(input.LastSessionPresent);
        var fingerprint = settings.Fingerprint(MosquittoBroker.UserName, "account");
        Assert.Equal(OutputSendStatus.Ack, (await session.SendAsync(new("{\"v\":42}"u8.ToArray(), topic, 1, fingerprint, "test"), deadline.Token)).Status);
        Assert.Equal("{\"v\":42}", await received.Task.WaitAsync(TimeSpan.FromSeconds(10), Token));
        await acknowledged.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.Equal(1, connector.Connections);
    }

    [Fact]
    public async Task FailedHandoffDisconnectsWithoutAckAndBrokerRedeliversInPersistentSession()
    {
        var broker = fixture.Require();
        var topic = MqttProbe.NewTopic("handoff-replay");
        var attempts = 0;
        var replay = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var acknowledged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failures = new ConcurrentQueue<string>();
        var input = new MqttInputReceiver([new(topic, 1, (message, _) =>
        {
            if (Interlocked.Increment(ref attempts) == 1) { return Task.FromResult(InputHandoffStatus.StorageFailed); }
            replay.TrySetResult(message.Dup);
            return Task.FromResult(InputHandoffStatus.Committed);
        })], TimeProvider.System, reason =>
        {
            failures.Enqueue(reason);
            if (reason == "acknowledged")
            { acknowledged.TrySetResult(); }
        });
        var connector = new CountingConnector();
        await using var session = new MqttSession(Settings(broker), NewId(), new Credentials(), TimeProvider.System, connector, input);
        using var deadline = MqttProbe.Timeout(Token);
        Assert.Equal(OutputSendStatus.Ack, (await session.MaintainAsync(deadline.Token)).Status);
        var (publisher, _) = await MqttProbe.ConnectAsync(MqttProbe.Options(broker, NewId()), Token);
        using (publisher)
        { await MqttProbe.PublishAsync(publisher, topic, "{\"v\":42}"u8.ToArray(), MqttQualityOfServiceLevel.AtLeastOnce, Token); }
        await OutputPipelineTests.UntilAsync(() => !session.IsConnected);
        Assert.False(acknowledged.Task.IsCompleted);
        Assert.Equal(OutputSendStatus.Ack, (await session.MaintainAsync(deadline.Token)).Status);
        Assert.True(input.LastSessionPresent);
        Assert.True(await replay.Task.WaitAsync(TimeSpan.FromSeconds(10), Token));
        await acknowledged.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.Equal(2, attempts);
        Assert.Equal(2, connector.Connections);
        Assert.Contains("handoffFailed", failures);
    }

    private static MqttConnectionSettings Settings(MosquittoBroker broker) => new()
    { ConnectionId = 1, Host = "127.0.0.1", Port = broker.Port, UsePassword = true };
    private static string NewId() => "input-session-" + Guid.NewGuid().ToString("N");
    private sealed class Credentials : ICredentialResolver
    {
        public MqttCredentialIdentity? DescribeMqtt(long connectionId) => new(MosquittoBroker.UserName, "account", "v1");
        public MqttCredential? ResolveMqtt(long connectionId, string recipient) => new(MosquittoBroker.UserName, MosquittoBroker.Password, "account", "v1");
    }
    private sealed class CountingConnector : IOutboundConnector
    {
        public int Connections { get; private set; }
        public async ValueTask<Stream> ConnectAsync(string host, int port, CancellationToken cancellationToken)
        {
            var stream = await new OutboundConnector(new Loopback()).ConnectAsync(host, port, cancellationToken);
            Connections++;
            return stream;
        }
    }
    private sealed class Loopback : IOutboundAddressPolicy
    {
        public bool IsAllowed(IPAddress address, int port) => IPAddress.IsLoopback(address);
    }
}
