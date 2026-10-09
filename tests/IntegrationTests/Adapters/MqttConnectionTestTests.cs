using System.Net;
using System.Net.Sockets;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Adapters.Mqtt;
using TenonEdge.IntegrationTests.Probes.Mqtt;
using TenonEdge.Runtime.Connections;

namespace TenonEdge.IntegrationTests.Adapters;

[Trait("Category", "RealBroker")]
public sealed class MqttConnectionTestTests(MosquittoBrokerFixture fixture) : IClassFixture<MosquittoBrokerFixture>
{
    private static readonly string[] SuccessfulSteps = ["success", "success", "skipped", "success", "success"];
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static MqttCredential Credential => new(MosquittoBroker.UserName, MosquittoBroker.Password, "fixture", "1");

    [Fact]
    public async Task TestDoesNotDisplaceExistingClientAndDoesNotChangePersistentSession()
    {
        var broker = fixture.Require();
        var id = "production-" + Guid.NewGuid().ToString("N");
        var (existing, _) = await MqttProbe.ConnectAsync(MqttProbe.Options(broker, id, cleanSession: false), Token);
        using (existing)
        {
            await MqttProbe.SubscribeAsync(existing, MqttProbe.NewTopic("persistent-probe-isolation"), Token);
            var probe = new MqttConnectionTest(new OutboundConnector(new Policy(true)), TimeProvider.System);
            var settings = Settings(broker.Port) with { ClientId = id };
            for (var index = 0; index < 3; index++)
            {
                var result = await probe.RunAsync(settings, Credential, Token);
                Assert.True(result.Success);
                Assert.Equal("success", result.Code);
                Assert.Equal(SuccessfulSteps, result.Steps.Select(step => step.Status));
                Assert.Equal(result.Steps[3].ElapsedMs, result.Steps[4].ElapsedMs);
                Assert.Equal(0, result.ConnAckCode);
                Assert.True(result.ElapsedMs >= 0);
                await existing.PingAsync(Token);
                Assert.True(existing.IsConnected);
            }
        }
        var (resumed, session) = await MqttProbe.ConnectAsync(MqttProbe.Options(broker, id, cleanSession: false), Token);
        using (resumed)
        { Assert.True(session.IsSessionPresent); }
    }

    [Fact]
    public async Task UntrustedTlsCertificateIsRejectedWithoutBypass()
    {
        var broker = fixture.Require();
        var probe = new MqttConnectionTest(new OutboundConnector(new Policy(true)), TimeProvider.System);
        var result = await probe.RunAsync(Settings(broker.TlsPort) with { Host = "localhost", Tls = true }, Credential, Token);
        Assert.False(result.Success);
        Assert.Equal("certificate.untrusted", result.Code);
        Assert.Null(result.ConnAckCode);
    }

    [Theory]
    [InlineData(SocketError.HostNotFound, "dns.failed")]
    [InlineData(SocketError.ConnectionRefused, "connection.refused")]
    [InlineData(SocketError.NetworkUnreachable, "connection.failed")]
    public async Task TransportFailuresAreClassifiedWithoutExceptionDetails(SocketError error, string expected)
    {
        var probe = new MqttConnectionTest(new FailingConnector(error), TimeProvider.System);
        var result = await probe.RunAsync(Settings(1883), Credential, Token);
        Assert.Equal(expected, result.Code);
        Assert.Null(result.ConnAckCode);
    }

    private sealed class FailingConnector(SocketError error) : IOutboundConnector
    {
        public ValueTask<Stream> ConnectAsync(string host, int port, CancellationToken cancellationToken) =>
            ValueTask.FromException<Stream>(new SocketException((int)error));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RealBrokerRejectsWrongOrMissingCredential(bool usePassword)
    {
        var broker = fixture.Require();
        var probe = new MqttConnectionTest(new OutboundConnector(new Policy(true)), TimeProvider.System);
        var result = await probe.RunAsync(Settings(broker.Port) with { UsePassword = usePassword },
            usePassword ? Credential with { Password = "fixture-wrong-secret" } : null, Token);
        Assert.False(result.Success);
        Assert.Equal("credential.rejected", result.Code);
        Assert.NotNull(result.ConnAckCode);
        Assert.DoesNotContain("fixture-wrong-secret", result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RevokedPolicyBlocksConnectionBeforeBrokerAccepts()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var probe = new MqttConnectionTest(new OutboundConnector(new Policy(false)), TimeProvider.System);
        var result = await probe.RunAsync(Settings(((IPEndPoint)listener.LocalEndpoint).Port), Credential, Token);
        Assert.Equal("outbound.denied", result.Code);
        Assert.Equal("success", result.Steps[0].Status);
        Assert.Equal("failed", result.Steps[1].Status);
        Assert.Equal("notReached", result.Steps[4].Status);
        Assert.Null(result.ConnAckCode);
        Assert.False(listener.Pending());
    }

    [Fact]
    public async Task SilentBrokerTimesOutAndClientIsClosedBeforeReturning()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var probe = new MqttConnectionTest(new OutboundConnector(new Policy(true)), TimeProvider.System);
        var operation = probe.RunAsync(Settings(((IPEndPoint)listener.LocalEndpoint).Port) with { TimeoutMs = 200 }, Credential, Token);
        using var accepted = await listener.AcceptTcpClientAsync(Token);
        var result = await operation;
        Assert.Equal("timeout", result.Code);
        Assert.Equal("failed", result.Steps.Single(step => step.Stage == "protocol").Status);
        Assert.Equal("unknown", result.Steps.Single(step => step.Stage == "authentication").Status);
        await AssertClosedAsync(accepted);
    }

    [Fact]
    public async Task RequestCancellationPropagatesAndClosesClient()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var probe = new MqttConnectionTest(new OutboundConnector(new Policy(true)), TimeProvider.System);
        var operation = probe.RunAsync(Settings(((IPEndPoint)listener.LocalEndpoint).Port), Credential, canceled.Token);
        using var accepted = await listener.AcceptTcpClientAsync(Token);
        await canceled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        await AssertClosedAsync(accepted);
    }

    [Fact]
    public async Task UnexpectedPublishIsRejectedWithoutWaitingForItsDeclaredBody()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var probe = new MqttConnectionTest(new OutboundConnector(new Policy(true)), TimeProvider.System);
        var operation = probe.RunAsync(Settings(((IPEndPoint)listener.LocalEndpoint).Port), Credential, Token);
        using var accepted = await listener.AcceptTcpClientAsync(Token);
        await accepted.GetStream().WriteAsync(new byte[] { 0x30, 0x7f }, Token);
        var result = await operation;
        Assert.Equal("protocol.invalid", result.Code);
        await AssertClosedAsync(accepted);
    }

    private static async Task AssertClosedAsync(TcpClient accepted)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var bytes = new byte[4096];
        while (await accepted.GetStream().ReadAsync(bytes, timeout.Token) != 0)
        { }
    }

    private static MqttConnectionSettings Settings(int port) => new()
    { ConnectionId = 1, Host = "127.0.0.1", Port = port, UsePassword = true };

    private sealed class Policy(bool allowed) : IOutboundAddressPolicy
    {
        public bool IsAllowed(IPAddress address, int port) => allowed;
    }
}
