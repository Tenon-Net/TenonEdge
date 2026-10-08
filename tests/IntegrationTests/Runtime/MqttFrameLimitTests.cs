using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using MQTTnet;
using MQTTnet.Formatter;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Adapters.Mqtt;
using TenonEdge.IntegrationTests.Probes.Mqtt;
using TenonEdge.Runtime.Connections;

namespace TenonEdge.IntegrationTests.Runtime;

public sealed class MqttFrameLimitTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task TcpAndTlsRejectOversizedDeclarationWithoutWaitingForItsBody(bool tls, bool fragmented)
    {
        using var authority = TestCertificateAuthority.Create("MQTT frame guard test");
        using var root = authority.RootCertificate;
        var issued = authority.IssueServerCertificate("localhost");
        using var certificate = X509Certificate2.CreateFromPem(issued.CertificatePem, issued.KeyPem);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var reports = 0;
        var messages = 0;
        using var client = new MqttClientFactory().CreateMqttClient(new MqttBoundedAdapterFactory(() => Interlocked.Increment(ref reports)));
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.DisconnectedAsync += _ => { disconnected.TrySetResult(); return Task.CompletedTask; };
        client.ApplicationMessageReceivedAsync += _ => { Interlocked.Increment(ref messages); return Task.CompletedTask; };
        using var deadline = MqttProbe.Timeout(Token);
        var options = new MqttClientOptionsBuilder().WithTcpServer("127.0.0.1", port)
            .WithProtocolVersion(MqttProtocolVersion.V311).WithClientId("frame-limit-test");
        if (tls)
        { options.WithTlsOptions(value => value.UseTls().WithTrustChain([root])); }
        var connecting = client.ConnectAsync(options.Build(), deadline.Token);
        using var peer = await listener.AcceptTcpClientAsync(deadline.Token);
        using var network = peer.GetStream();
        using var secure = tls ? new SslStream(network, leaveInnerStreamOpen: true) : null;
        if (secure is not null)
        { await secure.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, deadline.Token); }
        var stream = (Stream?)secure ?? network;
        Assert.Equal(0x10, (await ReadPacketAsync(stream, deadline.Token)).Header);
        await stream.WriteAsync(new byte[] { 0x20, 2, 0, 0 }, deadline.Token);
        Assert.Equal(MqttClientConnectResultCode.Success, (await connecting).ResultCode);
        var header = OversizedHeader();
        if (fragmented)
        {
            foreach (var value in header)
            { await stream.WriteAsync(new byte[] { value }, deadline.Token); }
        }
        else
        { await stream.WriteAsync(header, deadline.Token); }
        // 不发送声明的正文。旧实现会分配并等待正文；保护层必须只凭帧头就关闭连接。
        await disconnected.Task.WaitAsync(deadline.Token);
        Assert.False(client.IsConnected);
        Assert.Equal(1, Volatile.Read(ref reports));
        Assert.Equal(0, Volatile.Read(ref messages));
    }

    [Fact]
    public async Task ProductSessionCountsWireOversizeAndNeverHandsItToInput()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var oversized = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbacks = 0;
        var receiver = new MqttInputReceiver([new("test/in", 1, (_, _) =>
        {
            Interlocked.Increment(ref callbacks);
            return Task.FromResult(InputHandoffStatus.Rejected);
        })], TimeProvider.System, reason => { if (reason == "wireOversized") { oversized.TrySetResult(); } });
        var settings = new MqttConnectionSettings { ConnectionId = 1, Host = "127.0.0.1", Port = port };
        await using var session = new MqttSession(settings, "frame-product", new NoCredentials(), TimeProvider.System,
            new OutboundConnector(new Loopback()), receiver);
        using var deadline = MqttProbe.Timeout(Token);
        var maintaining = session.MaintainAsync(deadline.Token);
        using var peer = await listener.AcceptTcpClientAsync(deadline.Token);
        var stream = peer.GetStream();
        Assert.Equal(0x10, (await ReadPacketAsync(stream, deadline.Token)).Header);
        await stream.WriteAsync(new byte[] { 0x20, 2, 0, 0 }, deadline.Token);
        var subscribe = await ReadPacketAsync(stream, deadline.Token);
        Assert.Equal(0x82, subscribe.Header);
        await stream.WriteAsync(new byte[] { 0x90, 3, subscribe.Body[0], subscribe.Body[1], 1 }, deadline.Token);
        Assert.Equal(OutputSendStatus.Ack, (await maintaining).Status);
        await stream.WriteAsync(OversizedHeader(), deadline.Token);
        await oversized.Task.WaitAsync(deadline.Token);
        await OutputPipelineTests.UntilAsync(() => !session.IsConnected);
        Assert.Equal(0, Volatile.Read(ref callbacks));
    }

    [Fact]
    public async Task RejectedQosZeroDoesNotBlockFollowingPingResponse()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var dropped = 0;
        var callbacks = 0;
        using var client = new MqttClientFactory().CreateMqttClient(new MqttBoundedAdapterFactory(() => Assert.Fail("未超限。"),
            (header, _) =>
            {
                if (header != 0x30)
                { return true; }
                Interlocked.Increment(ref dropped);
                return false;
            }));
        client.ApplicationMessageReceivedAsync += _ => { Interlocked.Increment(ref callbacks); return Task.CompletedTask; };
        using var deadline = MqttProbe.Timeout(Token);
        var connecting = client.ConnectAsync(new MqttClientOptionsBuilder().WithTcpServer("127.0.0.1", port)
            .WithProtocolVersion(MqttProtocolVersion.V311).WithClientId("frame-admission-test").Build(), deadline.Token);
        using var peer = await listener.AcceptTcpClientAsync(deadline.Token);
        var stream = peer.GetStream();
        Assert.Equal(0x10, (await ReadPacketAsync(stream, deadline.Token)).Header);
        await stream.WriteAsync(new byte[] { 0x20, 2, 0, 0 }, deadline.Token);
        Assert.Equal(MqttClientConnectResultCode.Success, (await connecting).ResultCode);
        var ping = client.PingAsync(deadline.Token);
        Assert.Equal(0xc0, (await ReadPacketAsync(stream, deadline.Token)).Header);
        // 合法 QoS 0：单字节 Topic，正文总长 16384；紧接 PINGRESP，不能让丢弃留下半个包。
        var wire = new byte[4 + 16384 + 2];
        new byte[] { 0x30, 0x80, 0x80, 0x01, 0, 1, (byte)'a' }.CopyTo(wire, 0);
        wire[^2] = 0xd0;
        await stream.WriteAsync(wire, deadline.Token);
        await ping;
        Assert.True(client.IsConnected);
        Assert.Equal(1, Volatile.Read(ref dropped));
        Assert.Equal(0, Volatile.Read(ref callbacks));
    }

    [Theory]
    [InlineData(0x30)]
    [InlineData(0x32)]
    public async Task OutputOnlySessionRejectsUnsolicitedPublishHeaderBeforeBodyAndCanReconnect(byte header)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var settings = new MqttConnectionSettings { ConnectionId = 1, Host = "127.0.0.1", Port = port };
        await using var session = new MqttSession(settings, "output-only-frame", new NoCredentials(), TimeProvider.System,
            new OutboundConnector(new Loopback()));
        using var deadline = MqttProbe.Timeout(Token);
        var maintaining = session.MaintainAsync(deadline.Token);
        using var peer = await listener.AcceptTcpClientAsync(deadline.Token);
        var stream = peer.GetStream();
        Assert.Equal(0x10, (await ReadPacketAsync(stream, deadline.Token)).Header);
        await stream.WriteAsync(new byte[] { 0x20, 2, 0, 0 }, deadline.Token);
        Assert.Equal(OutputSendStatus.Ack, (await maintaining).Status);
        // 没有订阅，且完全不发送正文；应只凭固定头拒绝，不能留给库分配/排队。
        await stream.WriteAsync(new byte[] { header, 100 }, deadline.Token);
        using var rejectDeadline = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        rejectDeadline.CancelAfter(TimeSpan.FromSeconds(3));
        Assert.Equal(0, await stream.ReadAsync(new byte[1], rejectDeadline.Token));
        await OutputPipelineTests.UntilAsync(() => !session.IsConnected);
        maintaining = session.MaintainAsync(deadline.Token);
        using var nextPeer = await listener.AcceptTcpClientAsync(deadline.Token);
        var next = nextPeer.GetStream();
        Assert.Equal(0x10, (await ReadPacketAsync(next, deadline.Token)).Header);
        await next.WriteAsync(new byte[] { 0x20, 2, 0, 0 }, deadline.Token);
        var result = await maintaining;
        Assert.Equal(OutputSendStatus.Ack, result.Status);
        Assert.False(result.CountsTowardPoison);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static byte[] OversizedHeader()
    {
        var bytes = new List<byte> { 0x32 };
        var length = MqttBoundedAdapterFactory.MaximumRemainingLength + 1;
        do
        {
            var current = length % 128;
            length /= 128;
            bytes.Add((byte)(current | (length > 0 ? 0x80 : 0)));
        } while (length > 0);
        return bytes.ToArray();
    }
    private static async Task<(int Header, byte[] Body)> ReadPacketAsync(Stream stream, CancellationToken token)
    {
        var one = new byte[1];
        await stream.ReadExactlyAsync(one, token);
        var header = one[0];
        var length = 0;
        var shift = 0;
        do
        {
            await stream.ReadExactlyAsync(one, token);
            length |= (one[0] & 0x7f) << shift;
            shift += 7;
            Assert.InRange(shift, 7, 28);
        } while ((one[0] & 0x80) != 0);
        Assert.InRange(length, 0, 4096);
        var body = new byte[length];
        await stream.ReadExactlyAsync(body, token);
        return (header, body);
    }
    private sealed class Loopback : IOutboundAddressPolicy
    {
        public bool IsAllowed(IPAddress address, int port) => IPAddress.IsLoopback(address);
    }
    private sealed class NoCredentials : ICredentialResolver
    {
        public MqttCredentialIdentity? DescribeMqtt(long connectionId) => null;
        public MqttCredential? ResolveMqtt(long connectionId, string recipient) => null;
    }
}
