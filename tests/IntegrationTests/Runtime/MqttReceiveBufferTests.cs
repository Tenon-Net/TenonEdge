using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using MQTTnet;
using MQTTnet.Formatter;
using TenonEdge.Adapters.Mqtt;
using TenonEdge.IntegrationTests.Probes.Mqtt;
using TenonEdge.Runtime.Diagnostics;

namespace TenonEdge.IntegrationTests.Runtime;

public sealed class MqttReceiveBufferTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public async Task SlowCallbackKeepsBudgetAfterDisconnectAndNeverEntersLibraryQueue(bool qosOne, bool byteLimited, bool tls)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        var token = deadline.Token;
        using var authority = TestCertificateAuthority.Create("MQTT bounded receive test");
        using var root = authority.RootCertificate;
        var issued = authority.IssueServerCertificate("localhost");
        using var certificate = X509Certificate2.CreateFromPem(issued.CertificatePem, issued.KeyPem);
        var budget = new InFlightBudget(1024 * 1024, byteLimited ? 5000 : 65536);
        var entered = Signal();
        var release = Signal();
        var pressure = Signal();
        var callbacks = 0;
        var libraryCallbacks = 0;
        using var buffer = new MqttReceiveBuffer(budget, byteLimited ? 100 : 1, async (_, _) =>
        {
            Interlocked.Increment(ref callbacks);
            entered.TrySetResult();
            await release.Task; // 故意不响应取消，原始额度不能先释放。
        }, reason => { if (reason == (qosOne ? "bufferFull" : "bufferDropped")) { pressure.TrySetResult(); } });
        using var client = new MqttClientFactory().CreateMqttClient(new MqttBoundedAdapterFactory(() => Assert.Fail("未超限。"), receiveBuffer: buffer));
        client.ApplicationMessageReceivedAsync += _ => { Interlocked.Increment(ref libraryCallbacks); return Task.CompletedTask; };
        var disconnected = Signal();
        client.DisconnectedAsync += _ => { disconnected.TrySetResult(); return Task.CompletedTask; };
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var options = new MqttClientOptionsBuilder().WithTcpServer("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port)
            .WithProtocolVersion(MqttProtocolVersion.V311).WithClientId("bounded-receive-test");
        if (tls)
        { options.WithTlsOptions(value => value.UseTls().WithTrustChain([root])); }
        var connecting = client.ConnectAsync(options.Build(), token);
        using var peer = await listener.AcceptTcpClientAsync(token);
        using var network = peer.GetStream();
        using var secure = tls ? new SslStream(network, leaveInnerStreamOpen: true) : null;
        if (secure is not null)
        { await secure.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, token); }
        var stream = (Stream?)secure ?? network;
        await ReadPacketAsync(stream, token);
        await stream.WriteAsync(new byte[] { 0x20, 2, 0, 0 }, token);
        await connecting;
        try
        {
            await stream.WriteAsync(qosOne ? new byte[] { 0x32, 6, 0, 1, (byte)'a', 0, 1, 42 } : [0x30, 4, 0, 1, (byte)'a', 42], token);
            await entered.Task.WaitAsync(token);
            var reserved = budget.Status.ReservedBytes;
            Assert.True(reserved > 0);
            if (qosOne)
            {
                // 不发第二帧正文；必须在预算检查时就断连，不能先分配/等待正文。
                await stream.WriteAsync(new byte[] { 0x32, 6 }, token);
                await pressure.Task.WaitAsync(token);
                await disconnected.Task.WaitAsync(token);
            }
            else
            {
                var ping = client.PingAsync(token);
                Assert.Equal(0xc0, (await ReadPacketAsync(stream, token)).Header);
                await stream.WriteAsync(new byte[] { 0x30, 4, 0, 1, (byte)'a', 43, 0xd0, 0 }, token);
                await ping;
                await pressure.Task.WaitAsync(token);
                Assert.True(client.IsConnected);
                client.Dispose();
            }
            Assert.False(buffer.Completion.IsCompleted);
            Assert.Equal(reserved, budget.Status.ReservedBytes);
            Assert.Equal(1, Volatile.Read(ref callbacks));
            Assert.Equal(0, Volatile.Read(ref libraryCallbacks));
        }
        finally
        {
            client.Dispose();
            release.TrySetResult();
            await buffer.Completion.WaitAsync(token);
        }
        Assert.Equal(0, budget.Status.ReservedBytes);
    }

    [Fact]
    public async Task ManualAcknowledgementIsDeferredAndPreservesPacketIdentifier()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        var token = deadline.Token;
        var budget = new InFlightBudget(1024 * 1024, 65536);
        var entered = Signal();
        var release = Signal();
        using var buffer = new MqttReceiveBuffer(budget, 2, async (args, cancellation) =>
        {
            args.AutoAcknowledge = false;
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellation);
            await args.AcknowledgeAsync(cancellation);
        }, _ => { });
        using var client = new MqttClientFactory().CreateMqttClient(new MqttBoundedAdapterFactory(() => { }, receiveBuffer: buffer));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var connecting = client.ConnectAsync(new MqttClientOptionsBuilder().WithTcpServer("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port)
            .WithProtocolVersion(MqttProtocolVersion.V311).WithClientId("bounded-ack-test").Build(), token);
        using var peer = await listener.AcceptTcpClientAsync(token);
        var stream = peer.GetStream();
        await ReadPacketAsync(stream, token);
        await stream.WriteAsync(new byte[] { 0x20, 2, 0, 0 }, token);
        await connecting;
        try
        {
            await stream.WriteAsync(new byte[] { 0x32, 6, 0, 1, (byte)'a', 0x12, 0x34, 42 }, token);
            await entered.Task.WaitAsync(token);
            var reply = ReadPacketAsync(stream, token);
            Assert.False(reply.IsCompleted);
            release.TrySetResult();
            var ack = await reply;
            Assert.Equal(0x40, ack.Header);
            Assert.Equal(new byte[] { 0x12, 0x34 }, ack.Body);
        }
        finally
        {
            client.Dispose();
            release.TrySetResult();
            await buffer.Completion.WaitAsync(token);
        }
        Assert.Equal(0, budget.Status.ReservedBytes);
    }

    [Fact]
    public async Task QosOneBurstLargerThanWindowDrainsInOrderWithoutReconnect()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        var token = deadline.Token;
        var budget = new InFlightBudget(1024 * 1024, 5000);
        var entered = Signal();
        var release = Signal();
        var ids = new List<ushort>();
        using var buffer = new MqttReceiveBuffer(budget, 1, async (args, cancellation) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellation);
            ids.Add(args.PacketIdentifier);
            await args.AcknowledgeAsync(cancellation);
        }, reason => Assert.Fail("短时压力不能丢弃/断连：" + reason));
        using var client = new MqttClientFactory().CreateMqttClient(new MqttBoundedAdapterFactory(() => { }, receiveBuffer: buffer));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var connecting = client.ConnectAsync(new MqttClientOptionsBuilder().WithTcpServer("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port)
            .WithProtocolVersion(MqttProtocolVersion.V311).WithClientId("bounded-burst-test").Build(), token);
        using var peer = await listener.AcceptTcpClientAsync(token);
        var stream = peer.GetStream();
        await ReadPacketAsync(stream, token);
        await stream.WriteAsync(new byte[] { 0x20, 2, 0, 0 }, token);
        await connecting;
        try
        {
            var ping = client.PingAsync(token);
            Assert.Equal(0xc0, (await ReadPacketAsync(stream, token)).Header);
            for (byte id = 1; id <= 20; id++)
            { await stream.WriteAsync(new byte[] { 0x32, 6, 0, 1, (byte)'a', 0, id, 42 }, token); }
            await stream.WriteAsync(new byte[] { 0xd0, 0 }, token);
            await entered.Task.WaitAsync(token);
            Assert.False(ping.IsCompleted); // 控制帧还在受背压的 QoS 1 正文之后。
            release.TrySetResult();
            for (byte id = 1; id <= 20; id++)
            {
                var ack = await ReadPacketAsync(stream, token);
                Assert.Equal(0x40, ack.Header);
                Assert.Equal(new byte[] { 0, id }, ack.Body);
            }
            await ping;
            Assert.True(client.IsConnected);
        }
        finally
        {
            client.Dispose();
            release.TrySetResult();
            await buffer.Completion.WaitAsync(token);
        }
        Assert.Equal(Enumerable.Range(1, 20).Select(value => (ushort)value), ids);
        Assert.Equal(0, budget.Status.ReservedBytes);
        Assert.InRange(budget.Status.PeakReservedBytes, 1, 5000);
    }

    [Fact]
    public async Task DisconnectDuringBodyReadReleasesPendingReservationWithoutCallingInput()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        var token = deadline.Token;
        var budget = new InFlightBudget(1024 * 1024, 65536);
        using var buffer = new MqttReceiveBuffer(budget, 2, (_, _) => throw new InvalidOperationException("正文未到不能调用输入。"), _ => { });
        using var client = new MqttClientFactory().CreateMqttClient(new MqttBoundedAdapterFactory(() => { }, receiveBuffer: buffer));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var connecting = client.ConnectAsync(new MqttClientOptionsBuilder().WithTcpServer("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port)
            .WithProtocolVersion(MqttProtocolVersion.V311).WithClientId("bounded-partial-test").Build(), token);
        using var peer = await listener.AcceptTcpClientAsync(token);
        var stream = peer.GetStream();
        await ReadPacketAsync(stream, token);
        await stream.WriteAsync(new byte[] { 0x20, 2, 0, 0 }, token);
        await connecting;
        await stream.WriteAsync(new byte[] { 0x32, 6 }, token);
        await OutputPipelineTests.UntilAsync(() => budget.Status.ReservedBytes > 0);
        client.Dispose();
        await buffer.Completion.WaitAsync(token);
        Assert.Equal(0, budget.Status.ReservedBytes);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
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
}
