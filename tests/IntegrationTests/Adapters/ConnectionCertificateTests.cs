using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Adapters.Diagnostics;
using TenonEdge.Adapters.Http;
using TenonEdge.Adapters.Mqtt;
using TenonEdge.Runtime.Connections;

namespace TenonEdge.IntegrationTests.Adapters;

public sealed class ConnectionCertificateTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false, "localhost", -1, 1, "certificate.untrusted")]
    [InlineData(true, "localhost", -1, 1, "certificate.untrusted")]
    [InlineData(false, "wrong.invalid", -1, 1, "certificate.nameMismatch")]
    [InlineData(true, "wrong.invalid", -1, 1, "certificate.nameMismatch")]
    [InlineData(false, "localhost", -2, -1, "certificate.expired")]
    [InlineData(true, "localhost", -2, -1, "certificate.expired")]
    [InlineData(false, "localhost", 1, 2, "certificate.notYetValid")]
    [InlineData(true, "localhost", 1, 2, "certificate.notYetValid")]
    public async Task ActualTlsFailureIsClassifiedAndCredentialsNeverReachApplicationStream(
        bool http, string name, int notBeforeDays, int notAfterDays, string expected)
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
        var operation = RunProbeAsync();
        using var accepted = await listener.AcceptTcpClientAsync(token);
        using var stream = new SslStream(accepted.GetStream());
        var peer = ReadPeerAsync();
        var result = await operation;
        Assert.False(result.Success);
        Assert.Equal(expected, result.Code);
        Assert.Null(result.Reply);
        Assert.Equal("success", result.Steps.Single(step => step.Stage == "address").Status);
        Assert.Equal("success", result.Steps.Single(step => step.Stage == "connection").Status);
        var tls = result.Steps.Single(step => step.Stage == "tls");
        Assert.Equal("failed", tls.Status);
        Assert.Equal(expected, tls.Code);
        Assert.Equal("notReached", result.Steps.Single(step => step.Stage == "protocol").Status);
        Assert.Equal(0, await peer);
        Assert.DoesNotContain("fixture-never-send-before-tls", result.ToString(), StringComparison.Ordinal);

        async Task<(bool Success, string Code, int? Reply, ConnectionTestStep[] Steps)> RunProbeAsync()
        {
            var connector = new OutboundConnector(new LoopbackPolicy());
            if (http)
            {
                var result = await new HttpConnectionTest(new SharedConnections(), connector, TimeProvider.System).RunAsync(
                    new HttpRequestSettings { ConnectionId = 1, BaseAddress = $"https://localhost:{port}/" },
                    new HttpCredential(new(HttpCredentialKind.Bearer, null, null, "fixture", "1"), "fixture-never-send-before-tls"), token);
                return (result.Success, result.Code, result.StatusCode, result.Steps);
            }
            var mqtt = await new MqttConnectionTest(connector, TimeProvider.System).RunAsync(new MqttConnectionSettings
            { ConnectionId = 1, Host = "localhost", Port = port, Tls = true, UsePassword = true },
                new MqttCredential("test-user", "fixture-never-send-before-tls", "fixture", "1"), token);
            return (mqtt.Success, mqtt.Code, mqtt.ConnAckCode, mqtt.Steps);
        }

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
