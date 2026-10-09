using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Adapters.Mqtt;
using TenonEdge.Runtime.Connections;
using TenonEdge.Runtime.Diagnostics;
using TenonEdge.Runtime.Mapping;

namespace TenonEdge.IntegrationTests.Adapters;

public sealed class MqttReadPreviewWireTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("normal", "success")]
    [InlineData("invalidUtf8", "payload.invalidJson")]
    [InlineData("oversized", "payload.tooLarge")]
    [InlineData("refused", "subscription.rejected")]
    [InlineData("timeout", "timeout")]
    public async Task WireCaptureUsesIndependentCleanIdentityAndBoundsHostilePackets(string scenario, string expected)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var budget = new InFlightBudget(128 * 1024 * 1024, 64 * 1024 * 1024);
        var subscribed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clock = new FakeTimeProvider();
        var peer = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync(Token);
            var stream = socket.GetStream();
            var connect = await Packet(stream);
            Assert.Equal(0x10, connect.Header);
            Assert.True((connect.Body[7] & 2) != 0); // CleanSession
            var idLength = (connect.Body[10] << 8) | connect.Body[11];
            var id = Encoding.UTF8.GetString(connect.Body, 12, idLength);
            Assert.StartsWith("te-sample-", id, StringComparison.Ordinal);
            Assert.NotEqual("must-not-use-production-id", id);
            await stream.WriteAsync(new byte[] { 0x20, 2, 0, 0 }, Token);
            var subscribe = await Packet(stream);
            Assert.Equal(0x82, subscribe.Header);
            await stream.WriteAsync(new byte[] { 0x90, 3, subscribe.Body[0], subscribe.Body[1], scenario == "refused" ? (byte)0x80 : (byte)0 }, Token);
            subscribed.SetResult();
            if (scenario == "oversized")
            {
                // 声明超过上限，但不发送任何 Topic/正文。客户端必须在分配前关闭，不等待排空。
                await stream.WriteAsync(new byte[] { 0x30, 0xff, 0xff, 0xff, 0x7f }, Token);
            }
            else if (scenario is not ("refused" or "timeout"))
            {
                var body = scenario == "invalidUtf8" ? new byte[] { 0xff } : Encoding.UTF8.GetBytes("{\"value\":9223372036854775807}");
                var packet = new byte[5 + body.Length];
                packet[0] = 0x30;
                packet[1] = checked((byte)(body.Length + 3));
                packet[2] = 0;
                packet[3] = 1;
                packet[4] = (byte)'t';
                body.CopyTo(packet, 5);
                await stream.WriteAsync(packet, Token);
            }
            Assert.Equal(0, await stream.ReadAsync(new byte[1], Token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), Token));
        }, Token);
        var settings = new MqttInputSettings
        {
            ConnectionId = 1,
            Host = "127.0.0.1",
            Port = ((IPEndPoint)listener.LocalEndpoint).Port,
            ClientId = "must-not-use-production-id",
            TopicFilter = "t",
            Qos = 0,
            Fields = JsonSerializer.SerializeToElement(Array.Empty<object>())
        };
        var pending = new MqttReadPreview(new SharedConnections(), new OutboundConnector(new Policy()), new Resolver(),
            new JsonInputDecoderFactory(), budget, scenario == "timeout" ? clock : TimeProvider.System).RunAsync(settings, ClockSyncState.Unknown, Token);
        if (scenario == "timeout")
        {
            await subscribed.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            clock.Advance(TimeSpan.FromSeconds(30));
        }
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(5), Token);
        if (scenario == "timeout")
        { Assert.Equal(30000, result.ElapsedMs); }
        Assert.Equal(expected, result.Code);
        if (scenario == "normal")
        {
            Assert.False(result.Retained);
            Assert.Equal(0, result.Qos);
            Assert.Equal("{\"value\":9223372036854775807}", result.Preview!.RawJson);
        }
        Assert.Equal(0, budget.Status.ReservedBytes);
        await peer.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.False(listener.Pending());
    }

    private static async Task<(int Header, byte[] Body)> Packet(NetworkStream stream)
    {
        var one = new byte[1];
        await stream.ReadExactlyAsync(one, Token);
        var header = (int)one[0];
        var length = 0;
        for (var index = 0; index < 4; index++)
        {
            await stream.ReadExactlyAsync(one, Token);
            length |= (one[0] & 127) << (index * 7);
            if ((one[0] & 128) == 0)
            { break; }
        }
        Assert.InRange(length, 0, 65536);
        var body = new byte[length];
        await stream.ReadExactlyAsync(body, Token);
        return (header, body);
    }
    private sealed class Policy : IOutboundAddressPolicy { public bool IsAllowed(IPAddress address, int port) => IPAddress.IsLoopback(address); }
    private sealed class Resolver : ICredentialResolver
    {
        public MqttCredentialIdentity? DescribeMqtt(long connectionId) => null;
        public MqttCredential? ResolveMqtt(long connectionId, string recipient) => null;
    }
}
