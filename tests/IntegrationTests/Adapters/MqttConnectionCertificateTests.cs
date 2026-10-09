using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Adapters.Mqtt;
using TenonEdge.Runtime.Connections;

namespace TenonEdge.IntegrationTests.Adapters;

public sealed class MqttConnectionCertificateTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("localhost", -1, 1, "certificate.untrusted")]
    [InlineData("wrong.invalid", -1, 1, "certificate.nameMismatch")]
    [InlineData("localhost", -2, -1, "certificate.expired")]
    [InlineData("localhost", 1, 2, "certificate.notYetValid")]
    public async Task ActualTlsFailureIsClassifiedAndCredentialsNeverReachApplicationStream(
        string name, int notBeforeDays, int notAfterDays, string expected)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(name);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        var now = DateTimeOffset.UtcNow;
        using var certificate = request.CreateSelfSigned(now.AddDays(notBeforeDays), now.AddDays(notAfterDays));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        var token = deadline.Token;
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var probe = new MqttConnectionTest(new OutboundConnector(new LoopbackPolicy()), TimeProvider.System);
        var operation = probe.RunAsync(new MqttConnectionSettings
        { ConnectionId = 1, Host = "localhost", Port = port, Tls = true, UsePassword = true },
            new MqttCredential("test-user", "fixture-never-send-before-tls", "fixture", "1"), token);
        using var accepted = await listener.AcceptTcpClientAsync(token);
        using var stream = new SslStream(accepted.GetStream());
        var peer = ReadPeerAsync();
        var result = await operation;
        Assert.False(result.Success);
        Assert.Equal(expected, result.Code);
        Assert.Null(result.ConnAckCode);
        Assert.Equal("success", result.Steps.Single(step => step.Stage == "address").Status);
        Assert.Equal("success", result.Steps.Single(step => step.Stage == "connection").Status);
        var tls = result.Steps.Single(step => step.Stage == "tls");
        Assert.Equal("failed", tls.Status);
        Assert.Equal(expected, tls.Code);
        Assert.Equal("notReached", result.Steps.Single(step => step.Stage == "protocol").Status);
        Assert.Equal(0, await peer);
        Assert.DoesNotContain("fixture-never-send-before-tls", result.ToString(), StringComparison.Ordinal);

        async Task<int> ReadPeerAsync()
        {
            try
            {
                await stream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, token);
                return await stream.ReadAsync(new byte[1], token);
            }
            catch (Exception exception) when (exception is AuthenticationException or IOException)
            { return 0; } // TLS 拒绝或关闭；收到应用字节则上述返回值非零，测试必须失败。
        }
    }

    private sealed class LoopbackPolicy : IOutboundAddressPolicy
    {
        public bool IsAllowed(IPAddress address, int port) => IPAddress.IsLoopback(address);
    }
}
