using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Time.Testing;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Adapters.Mqtt;
using TenonEdge.IntegrationTests.Probes.Mqtt;
using TenonEdge.Runtime.Connections;

namespace TenonEdge.IntegrationTests.Adapters;

public sealed class MqttTestMessageSenderTests(MosquittoBrokerFixture fixture) : IClassFixture<MosquittoBrokerFixture>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static MqttCredential Credential => new(MosquittoBroker.UserName, MosquittoBroker.Password, "fixture-account", "v1");
    private static OutputMessage Message(MqttConnectionSettings settings, string topic, int qos, MqttCredential? credential = null) =>
        new("{\"value\":9223372036854775807}"u8.ToArray(), topic, qos, settings.Fingerprint(credential?.Username, credential?.AccountMarker), "test");

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [Trait("Category", "RealBroker")]
    public async Task SendsExactlyOneFrozenMessageWithoutReplacingTheProductionClientId(int qos, bool snapshotRequiresCredential)
    {
        var broker = fixture.Require();
        var topic = MqttProbe.NewTopic("test-message");
        var clientId = "existing-" + Guid.NewGuid().ToString("N");
        var (subscriber, _) = await MqttProbe.ConnectAsync(MqttProbe.Options(broker, clientId), Token);
        using (subscriber)
        {
            var received = ReceivedMessages.Attach(subscriber);
            await MqttProbe.SubscribeAsync(subscriber, topic, Token);
            var settings = new MqttConnectionSettings { ConnectionId = 1, Host = "127.0.0.1", Port = broker.Port, ClientId = clientId, UsePassword = snapshotRequiresCredential };
            var connector = new Connector();
            var message = Message(settings, topic, qos, Credential);
            var result = await new MqttTestMessageSender(connector, TimeProvider.System).SendAsync(settings, Credential, message, Token);
            Assert.True(result.Sent);
            Assert.Equal(qos == 1, result.BrokerAcknowledged);
            Assert.Equal(qos == 1 ? "mqtt.puback" : "mqtt.writtenWithoutAck", result.Code);
            var wire = await received.NextAsync(Token);
            Assert.Equal(message.Payload.ToArray(), wire.ApplicationMessage.Payload.ToArray());
            Assert.False(wire.ApplicationMessage.Retain);
            Assert.True(subscriber.IsConnected);
            Assert.Equal(1, connector.Calls);
            Assert.Empty(await received.CollectAsync(TimeSpan.FromMilliseconds(150), Token));
        }
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("cancel")]
    [InlineData("hostile")]
    public async Task MissingPubackCancellationAndHostilePacketCloseWithoutRetry(string scenario)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var clock = new FakeTimeProvider();
        var connector = new Connector();
        var settings = new MqttConnectionSettings { ConnectionId = 1, Host = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port, ClientId = "never-use-this", TimeoutMs = 1000 };
        var pending = new MqttTestMessageSender(connector, clock).SendAsync(settings, null, Message(settings, "test/topic", 1), stop.Token);
        using var peer = await listener.AcceptTcpClientAsync(Token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), Token);
        var stream = peer.GetStream();
        var connect = await Packet(stream);
        Assert.Equal(0x10, connect.Header);
        Assert.True((connect.Body[7] & 2) != 0);
        var clientId = Encoding.UTF8.GetString(connect.Body, 12, (connect.Body[10] << 8) | connect.Body[11]);
        Assert.StartsWith("te-send-test-", clientId, StringComparison.Ordinal);
        await stream.WriteAsync(new byte[] { 0x20, 2, 0, 0 }, Token);
        Assert.Equal(0x32, (await Packet(stream)).Header);
        if (scenario == "cancel")
        {
            await stop.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5), Token));
        }
        else
        {
            if (scenario == "timeout")
            { clock.Advance(TimeSpan.FromSeconds(1)); }
            else
            { await stream.WriteAsync(new byte[] { 0x30, 0xff, 0xff, 0xff, 0x7f }, Token); }
            var result = await pending.WaitAsync(TimeSpan.FromSeconds(5), Token);
            Assert.False(result.Sent);
            Assert.False(result.BrokerAcknowledged);
            Assert.Equal(scenario == "timeout" ? "mqtt.timeoutUnknown" : "mqtt.deliveryUnknown", result.Code);
        }
        Assert.Equal(0, await stream.ReadAsync(new byte[1], Token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), Token));
        Assert.Equal(1, connector.Calls);
        Assert.False(listener.Pending());
    }

    [Fact]
    public async Task RecipientChangeAndOutboundDenialDoNotProduceASecondAttempt()
    {
        var settings = new MqttConnectionSettings { ConnectionId = 1, Host = "127.0.0.1" };
        var connector = new Connector { Deny = true };
        var sender = new MqttTestMessageSender(connector, TimeProvider.System);
        var changed = await sender.SendAsync(settings, null, Message(settings, "test/topic", 1) with { Fingerprint = "different" }, Token);
        Assert.Equal("recipient.changed", changed.Code);
        Assert.Equal(0, connector.Calls);
        var denied = await sender.SendAsync(settings, null, Message(settings, "test/topic", 1), Token);
        Assert.Equal("connection.rejected", denied.Code);
        Assert.False(denied.Sent);
        Assert.Equal(1, connector.Calls);
    }

    [Theory]
    [InlineData("wildcard")]
    [InlineData("qos")]
    [InlineData("empty")]
    [InlineData("credential")]
    public async Task InvalidFrozenMessagesNeverReachTheConnector(string scenario)
    {
        var settings = new MqttConnectionSettings { ConnectionId = 1, Host = "127.0.0.1" };
        var connector = new Connector { Deny = true };
        var message = Message(settings, "test/topic", 1);
        message = scenario switch
        {
            "wildcard" => message with { Address = "test/#" },
            "qos" => message with { QualityOfService = 2 },
            "empty" => message with { Payload = ReadOnlyMemory<byte>.Empty },
            _ => message,
        };
        await Assert.ThrowsAsync<ArgumentException>(() => new MqttTestMessageSender(connector, TimeProvider.System)
            .SendAsync(settings, scenario == "credential" ? Credential with { Username = "" } : null, message, Token));
        Assert.Equal(0, connector.Calls);
    }

    private static async Task<(byte Header, byte[] Body)> Packet(Stream stream)
    {
        using var deadline = MqttProbe.Timeout(Token, TimeSpan.FromSeconds(5));
        var one = new byte[1];
        await stream.ReadExactlyAsync(one, deadline.Token);
        var header = one[0];
        var length = 0;
        var shift = 0;
        do
        {
            await stream.ReadExactlyAsync(one, deadline.Token);
            length |= (one[0] & 0x7f) << shift;
            shift += 7;
            Assert.InRange(shift, 7, 28);
        } while ((one[0] & 0x80) != 0);
        Assert.InRange(length, 0, 4096);
        var body = new byte[length];
        await stream.ReadExactlyAsync(body, deadline.Token);
        return (header, body);
    }

    private sealed class Connector : IOutboundConnector
    {
        private readonly OutboundConnector _inner = new(new Loopback());
        public int Calls { get; private set; }
        public bool Deny { get; init; }
        public ValueTask<Stream> ConnectAsync(string host, int port, CancellationToken cancellationToken)
        {
            Calls++;
            if (Deny)
            { throw new OutboundAccessDeniedException(); }
            return _inner.ConnectAsync(host, port, cancellationToken);
        }
    }
    private sealed class Loopback : IOutboundAddressPolicy
    { public bool IsAllowed(IPAddress address, int port) => IPAddress.IsLoopback(address); }
}
