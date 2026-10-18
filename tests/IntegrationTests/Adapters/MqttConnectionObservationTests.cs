using System.Net;
using Microsoft.Extensions.Time.Testing;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Adapters.Mqtt;
using TenonEdge.IntegrationTests.Probes.Mqtt;
using TenonEdge.IntegrationTests.Runtime;
using TenonEdge.Runtime.Connections;

namespace TenonEdge.IntegrationTests.Adapters;

[Trait("Category", "RealBroker")]
public sealed class MqttConnectionObservationTests(MosquittoBrokerFixture fixture) : IClassFixture<MosquittoBrokerFixture>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ActualConnectAndPingUpdateSuccessButNoOpMaintainDoesNot()
    {
        var broker = fixture.Require();
        var time = new FakeTimeProvider();
        var connector = new CountingConnector();
        var settings = Settings(broker.Port);
        await using var session = new MqttSession(settings, ClientId(), new Credentials(), time, connector);
        Assert.Equal(new(ConnectionCommunicationState.Connecting), session.Observation);
        Assert.Equal(OutputSendStatus.Ack, (await session.MaintainAsync(Token)).Status);
        var first = session.Observation;
        Assert.Equal(ConnectionCommunicationState.Connected, first.State);
        Assert.Equal(time.GetUtcNow(), first.LastSuccessAt);
        time.Advance(TimeSpan.FromMilliseconds(100));
        Assert.Equal(OutputSendStatus.Ack, (await session.MaintainAsync(Token)).Status);
        Assert.Equal(first, session.Observation);
        time.Advance(TimeSpan.FromMilliseconds(settings.PingIntervalMs));
        Assert.Equal(OutputSendStatus.Ack, (await session.MaintainAsync(Token)).Status);
        Assert.Equal(time.GetUtcNow(), session.Observation.LastSuccessAt);
        Assert.Equal(1, connector.Created);
        // 主动释放不会制造 mqtt.disconnected 错误，最后一次成功仍可核对。
        await session.DisposeAsync();
        Assert.Equal(ConnectionCommunicationState.Connecting, session.Observation.State);
        Assert.Null(session.Observation.LastErrorAt);
    }

    [Fact]
    public async Task IdleConnectionLossIsObservedWithoutAnotherMaintainOrPublishAndOriginalSessionCanRecover()
    {
        var broker = fixture.Require();
        var connector = new CountingConnector();
        var time = new FakeTimeProvider();
        await using var session = new MqttSession(Settings(broker.Port), ClientId(), new Credentials(), time, connector);
        Assert.Equal(OutputSendStatus.Ack, (await session.MaintainAsync(Token)).Status);
        var oldStream = connector.Last!;
        time.Advance(TimeSpan.FromSeconds(1));
        oldStream.Dispose();
        await OutputPipelineTests.UntilAsync(() => session.Observation.State == ConnectionCommunicationState.Degraded);
        var failed = session.Observation;
        Assert.Equal("mqtt.disconnected", failed.LastErrorCode);
        Assert.Equal(time.GetUtcNow(), failed.LastErrorAt);
        Assert.Equal(1, connector.Created); // 回调不自动重连。
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(OutputSendStatus.Ack, (await session.MaintainAsync(Token)).Status);
        Assert.Equal(2, connector.Created);
        Assert.NotSame(oldStream, connector.Last);
        oldStream.Dispose();
        Assert.Equal(OutputSendStatus.Ack, (await session.MaintainAsync(Token)).Status);
        Assert.Equal(ConnectionCommunicationState.Connected, session.Observation.State);
        Assert.Equal(time.GetUtcNow(), session.Observation.LastSuccessAt);
        Assert.Equal(failed.LastErrorAt, session.Observation.LastErrorAt);
        Assert.Equal(failed.LastErrorCode, session.Observation.LastErrorCode);
    }

    [Fact]
    public async Task SilentNetworkBlackholeIsDetectedByIdleKeepAliveWithoutPollingObservationDoingIo()
    {
        var broker = fixture.Require();
        await using var proxy = TcpFaultProxy.Start(broker.Port);
        var connector = new CountingConnector();
        await using var session = new MqttSession(Settings(proxy.Port) with { PingIntervalMs = 1000 }, ClientId(), new Credentials(), TimeProvider.System, connector);
        Assert.Equal(OutputSendStatus.Ack, (await session.MaintainAsync(Token)).Status);
        var succeeded = session.Observation.LastSuccessAt;
        proxy.Freeze();
        await OutputPipelineTests.UntilAsync(() => session.Observation.State == ConnectionCommunicationState.Degraded);
        Assert.Equal("mqtt.disconnected", session.Observation.LastErrorCode);
        Assert.Equal(succeeded, session.Observation.LastSuccessAt);
        Assert.NotNull(session.Observation.LastErrorAt);
        Assert.Equal(1, connector.Created);
    }

    [Fact]
    public async Task BrokerAuthenticationFailureUsesOnlySafeClassification()
    {
        var broker = fixture.Require();
        await using var session = new MqttSession(Settings(broker.Port), ClientId(), new Credentials("fixture-wrong-secret"), TimeProvider.System, new CountingConnector());
        Assert.NotEqual(OutputSendStatus.Ack, (await session.MaintainAsync(Token)).Status);
        Assert.Equal(ConnectionCommunicationState.Degraded, session.Observation.State);
        Assert.Null(session.Observation.LastSuccessAt);
        Assert.True(session.Observation.LastErrorCode is "mqtt.connectionRejected" or "mqtt.communicationFailed" or "mqtt.authenticationFailed");
        Assert.DoesNotContain("fixture-wrong-secret", session.Observation.ToString(), StringComparison.Ordinal);
    }

    private static string ClientId() => "observation-" + Guid.NewGuid().ToString("N");
    private static MqttConnectionSettings Settings(int port) => new() { ConnectionId = 1, Host = "127.0.0.1", Port = port, UsePassword = true, TimeoutMs = 5000, PingIntervalMs = 5000 };
    private sealed class Credentials(string password = MosquittoBroker.Password) : ICredentialResolver
    {
        public MqttCredential? ResolveMqtt(long connectionId, string recipient) => new(MosquittoBroker.UserName, password, "fixture", "v1");
        public MqttCredentialIdentity? DescribeMqtt(long connectionId) => new(MosquittoBroker.UserName, "fixture", "v1");
    }
    private sealed class CountingConnector : IOutboundConnector
    {
        internal int Created;
        internal Stream? Last;
        public async ValueTask<Stream> ConnectAsync(string host, int port, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Created);
            return Last = await new OutboundConnector(new Loopback()).ConnectAsync(host, port, cancellationToken);
        }
    }
    private sealed class Loopback : IOutboundAddressPolicy
    { public bool IsAllowed(IPAddress address, int port) => IPAddress.IsLoopback(address); }
}
