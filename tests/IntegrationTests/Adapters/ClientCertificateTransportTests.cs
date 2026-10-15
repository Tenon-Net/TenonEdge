using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using MQTTnet;
using MQTTnet.Exceptions;
using MQTTnet.Formatter;
using MQTTnet.Protocol;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Adapters.Diagnostics;
using TenonEdge.Adapters.Http;
using TenonEdge.Adapters.Mqtt;
using TenonEdge.Runtime.Connections;

namespace TenonEdge.IntegrationTests.Adapters;

/// <summary>产品传输与真实 SslStream 对端；MQTT 对端只实现测试所需的 CONNECT/PUBLISH/PUBACK，不是 Broker 验收。</summary>
public sealed class ClientCertificateTransportTests
{
    public static TheoryData<bool, string> Cases { get; } = new(
        from http in new[] { true, false }
        from scenario in new[] { "pem", "pfx", "pemRsa", "pfxRsa", "missing", "wrongClient", "missingIssuer", "untrustedServer", "serverName", "expiredServer", "serverUsage" }
        select (http, scenario));

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task MutualTlsRequiresBothIdentitiesAndDoesNotFetchCertificateUrls(bool http, string scenario)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        var token = deadline.Token;
        using var urls = new TcpListener(IPAddress.Loopback, 0);
        urls.Start();
        var url = $"http://127.0.0.1:{((IPEndPoint)urls.LocalEndpoint).Port}/certificate-data";
        var downloads = 0;
        using var monitorStop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var monitor = MonitorAsync();
        try
        {
            using var rootKey = ECDsa.Create();
            using var root = ConnectionTrustTests.Certificate(rootKey, "client-root-" + Guid.NewGuid().ToString("N"), authority: true);
            using var intermediateKey = ECDsa.Create();
            using var intermediate = ConnectionTrustTests.Certificate(intermediateKey, "client-issuer-" + Guid.NewGuid().ToString("N"), authority: true, issuer: root);
            using var clientKey = scenario.EndsWith("Rsa", StringComparison.Ordinal) ? (AsymmetricAlgorithm)RSA.Create(2048) : ECDsa.Create();
            using var clientLeaf = IssueClient();
            using var publicIntermediate = X509CertificateLoader.LoadCertificate(intermediate.RawData);
            using var publicRoot = X509CertificateLoader.LoadCertificate(root.RawData);
            using var wrongKey = ECDsa.Create();
            using var wrongRoot = ConnectionTrustTests.Certificate(wrongKey, "wrong-client-root", authority: true);
            using var serverKey = ECDsa.Create();
            // 此证书由客户端 CA 签发；仍必须单独信任服务器，不能把上传客户端链变成服务器信任。
            using var server = ConnectionTrustTests.Certificate(serverKey, scenario == "serverName" ? "wrong.invalid" : "localhost",
                issuer: root, expired: scenario == "expiredServer", clientOnly: scenario == "serverUsage");
            var trust = scenario == "untrustedServer" ? null : new TlsTrustSnapshot(TlsTrustMode.PinnedCertificate,
                Convert.ToBase64String(server.RawData), "fixture-trust");
            var pem = clientLeaf.ExportCertificatePem() + (scenario == "missingIssuer" ? "" : "\n" + intermediate.ExportCertificatePem()) + "\n" + root.ExportCertificatePem();
            var pfx = new X509Certificate2Collection { clientLeaf, publicIntermediate, publicRoot }.Export(X509ContentType.Pfx, "fixture-password")!;
            using var clientCertificate = scenario == "missing" ? null : scenario.StartsWith("pfx", StringComparison.Ordinal)
                ? TlsClientCertificate.FromPkcs12(pfx, "fixture-password", DateTimeOffset.UtcNow)
                : TlsClientCertificate.FromPem(pem, clientKey.ExportPkcs8PrivateKeyPem(), DateTimeOffset.UtcNow);
            CryptographicOperations.ZeroMemory(pfx);
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var applicationBytes = 0;
            string? receivedIdentity = null;
            string? serverFailure = null;
            var connections = 0;
            var peer = PeerAsync();
            var connector = new CountingConnector(new OutboundConnector(new ConnectionTrustTests.LoopbackPolicy()), () => Interlocked.Increment(ref connections));
            var failure = await Record.ExceptionAsync(async () =>
            {
                if (http)
                {
                    using var transport = OutboundHttpTransport.CreateClient(connector, TimeSpan.FromSeconds(5), trust, clientCertificate);
                    using var request = new HttpRequestMessage(HttpMethod.Post, $"https://localhost:{port}/telemetry")
                    { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
                    using var response = await transport.SendAsync(request, token);
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                }
                else
                {
                    using var mqtt = new MqttClientFactory().CreateMqttClient(new MqttBoundedAdapterFactory(() => { }, clientCertificate: clientCertificate));
                    var options = new MqttClientOptionsBuilder().WithTcpServer("localhost", port)
                        .WithStreamProvider(new MqttStreamProvider(connector)).WithProtocolVersion(MqttProtocolVersion.V311)
                        .WithClientId("fixture-" + Guid.NewGuid().ToString("N")).WithCleanSession().WithKeepAlivePeriod(TimeSpan.Zero)
                        .WithTlsOptions(tls => tls.UseTls().WithCertificateValidationHandler(args =>
                        {
                            serverFailure = ServerCertificateTrust.Evaluate(args.Certificate, args.Chain, args.SslPolicyErrors, trust);
                            return serverFailure is null;
                        })).Build();
                    var connected = await mqtt.ConnectAsync(options, token);
                    Assert.Equal(MqttClientConnectResultCode.Success, connected.ResultCode);
                    var result = await mqtt.PublishAsync(new MqttApplicationMessageBuilder().WithTopic("tls/client")
                        .WithPayload("{}").WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).Build(), token);
                    Assert.True(result.IsSuccess);
                    await mqtt.DisconnectAsync(new MqttClientDisconnectOptions(), token);
                }
            });
            await peer;
            if (scenario is "pem" or "pfx" or "pemRsa" or "pfxRsa")
            {
                Assert.Null(failure);
                Assert.True(applicationBytes > 0);
                Assert.Equal(clientLeaf.GetCertHashString(HashAlgorithmName.SHA256), receivedIdentity);
            }
            else
            {
                Assert.NotNull(failure);
                Assert.False(deadline.IsCancellationRequested, "应由 TLS 验证明确拒绝，不能等测试超时。");
                Assert.Equal(0, applicationBytes);
                if (!http && scenario.StartsWith("server", StringComparison.Ordinal))
                { Assert.NotNull(serverFailure); }
            }
            Assert.Equal(1, connections);
            Assert.Equal(0, Volatile.Read(ref downloads));

            X509Certificate2 IssueClient()
            {
                if (clientKey is ECDsa ec)
                { return ConnectionTrustTests.Certificate(ec, "client-account", issuer: intermediate, clientOnly: true, issuerUrl: url, revocationUrl: url); }
                var rsa = (RSA)clientKey;
                var request = new CertificateRequest("CN=client-account", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
                request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
                request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.2")], true));
                using var issued = request.Create(intermediate.SubjectName, X509SignatureGenerator.CreateForECDsa(intermediateKey),
                    DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1), RandomNumberGenerator.GetBytes(16));
                return issued.CopyWithPrivateKey(rsa);
            }

            async Task PeerAsync()
            {
                using var accepted = await listener.AcceptTcpClientAsync(token);
                using var stream = new SslStream(accepted.GetStream());
                var clientPolicy = new X509ChainPolicy
                {
                    TrustMode = X509ChainTrustMode.CustomRootTrust,
                    RevocationMode = X509RevocationMode.NoCheck,
                    DisableCertificateDownloads = true,
                };
                clientPolicy.CustomTrustStore.Add(scenario == "wrongClient" ? wrongRoot : publicRoot);
                clientPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.2"));
                try
                {
                    await stream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                    {
                        ServerCertificate = server,
                        ClientCertificateRequired = true,
                        CertificateChainPolicy = clientPolicy,
                        RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
                        {
                            receivedIdentity = certificate?.GetCertHashString(HashAlgorithmName.SHA256);
                            return certificate is not null && errors == SslPolicyErrors.None;
                        },
                    }, token);
                    if (http)
                    {
                        var bytes = new List<byte>();
                        var buffer = new byte[1];
                        while (bytes.Count < 8192)
                        {
                            if (await stream.ReadAsync(buffer, token) == 0)
                            { return; }
                            bytes.Add(buffer[0]);
                            if (bytes.Count >= 4 && bytes.TakeLast(4).SequenceEqual("\r\n\r\n"u8.ToArray()))
                            { break; }
                        }
                        var headers = Encoding.ASCII.GetString(bytes.ToArray());
                        Assert.StartsWith("POST /telemetry HTTP/1.1", headers, StringComparison.Ordinal);
                        Assert.DoesNotContain("Authorization:", headers, StringComparison.OrdinalIgnoreCase);
                        var payload = new byte[2];
                        await stream.ReadExactlyAsync(payload, token);
                        Assert.Equal("{}"u8.ToArray(), payload);
                        applicationBytes = bytes.Count + payload.Length;
                        await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(), token);
                    }
                    else
                    {
                        var connect = await ReadFrameAsync(stream, 0x10, token);
                        Assert.Equal(0, connect[7] & 0xc0); // 无用户名/密码，认证只由 TLS 完成。
                        applicationBytes += connect.Length;
                        await stream.WriteAsync(new byte[] { 0x20, 2, 0, 0 }, token);
                        var publish = await ReadFrameAsync(stream, 0x32, token);
                        var topicLength = (publish[0] << 8) | publish[1];
                        Assert.Equal("tls/client", Encoding.UTF8.GetString(publish, 2, topicLength));
                        Assert.Equal("{}", Encoding.UTF8.GetString(publish, topicLength + 4, publish.Length - topicLength - 4));
                        await stream.WriteAsync(new byte[] { 0x40, 2, publish[topicLength + 2], publish[topicLength + 3] }, token);
                        _ = await ReadFrameAsync(stream, 0xe0, token);
                    }
                }
                catch (Exception exception) when (exception is AuthenticationException or IOException) { }
            }
        }
        finally
        {
            await monitorStop.CancelAsync();
            await monitor;
        }

        async Task MonitorAsync()
        {
            try
            {
                while (true)
                {
                    using var connection = await urls.AcceptTcpClientAsync(monitorStop.Token);
                    Interlocked.Increment(ref downloads);
                    var stream = connection.GetStream();
                    _ = await stream.ReadAsync(new byte[8192], monitorStop.Token);
                    await stream.WriteAsync("HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(), monitorStop.Token);
                }
            }
            catch (OperationCanceledException) when (monitorStop.IsCancellationRequested) { }
        }
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task CertificateCannotSilentlyDowngradeToPlaintext(bool http, bool synchronous)
    {
        using var key = ECDsa.Create();
        using var certificate = ConnectionTrustTests.Certificate(key, "client", clientOnly: true);
        using var material = TlsClientCertificate.FromPem(certificate.ExportCertificatePem(), key.ExportPkcs8PrivateKeyPem(), DateTimeOffset.UtcNow);
        var connector = new NeverConnect();
        if (http)
        {
            using var transport = OutboundHttpTransport.CreateClient(connector, TimeSpan.FromSeconds(2), clientCertificate: material);
            using var request = new HttpRequestMessage(HttpMethod.Get, "http://localhost:12345/");
            if (synchronous)
            { Assert.Throws<ArgumentException>(() => transport.Send(request, TestContext.Current.CancellationToken)); }
            else
            { await Assert.ThrowsAsync<ArgumentException>(() => transport.SendAsync(request, TestContext.Current.CancellationToken)); }
        }
        else
        {
            using var mqtt = new MqttClientFactory().CreateMqttClient(new MqttBoundedAdapterFactory(() => { }, clientCertificate: material));
            var options = new MqttClientOptionsBuilder().WithTcpServer("localhost", 12345).WithProtocolVersion(MqttProtocolVersion.V311)
                .WithStreamProvider(new MqttStreamProvider(connector)).Build();
            await Assert.ThrowsAsync<ArgumentException>(() => mqtt.ConnectAsync(options, TestContext.Current.CancellationToken));
        }
        Assert.Equal(0, connector.Attempts);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CertificateDoesNotBypassActualAddressPolicy(bool http)
    {
        using var key = ECDsa.Create();
        using var certificate = ConnectionTrustTests.Certificate(key, "client", clientOnly: true);
        using var material = TlsClientCertificate.FromPem(certificate.ExportCertificatePem(), key.ExportPkcs8PrivateKeyPem(), DateTimeOffset.UtcNow);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var connector = new OutboundConnector(new DenyAll());
        var failure = await Record.ExceptionAsync(() => ConnectAsync(http, connector, port, material, TestContext.Current.CancellationToken));
        Assert.NotNull(failure);
        Assert.True(HasCause<OutboundAccessDeniedException>(failure));
        Assert.False(listener.Pending());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CancellingStalledTlsClosesTheOriginalSocketAndDoesNotDisposeBorrowedCredential(bool http)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        using var key = ECDsa.Create();
        using var certificate = ConnectionTrustTests.Certificate(key, "client", clientOnly: true);
        using var material = TlsClientCertificate.FromPem(certificate.ExportCertificatePem(), key.ExportPkcs8PrivateKeyPem(), DateTimeOffset.UtcNow);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var connecting = ConnectAsync(http, new OutboundConnector(new ConnectionTrustTests.LoopbackPolicy()), port, material, cancellation.Token);
        using var accepted = await listener.AcceptTcpClientAsync(deadline.Token);
        var stream = accepted.GetStream();
        var buffer = new byte[8192];
        Assert.True(await stream.ReadAsync(buffer, deadline.Token) > 0); // 已发送 ClientHello；对端故意不回复。
        await cancellation.CancelAsync();
        var failure = await Record.ExceptionAsync(() => connecting);
        Assert.NotNull(failure);
        Assert.True(HasCause<OperationCanceledException>(failure) || HasCause<MqttCommunicationTimedOutException>(failure));
        try
        { while (await stream.ReadAsync(buffer, deadline.Token) != 0) { } }
        catch (IOException) { } // TCP reset 与 EOF 都是实际关闭；测试期限到达不能算关闭。
        Assert.False(deadline.IsCancellationRequested);
        material.ValidateForUse(DateTimeOffset.UtcNow);
        Assert.True(material.Context(DateTimeOffset.UtcNow).TargetCertificate.HasPrivateKey);
        Assert.False(listener.Pending());
    }

    [Fact]
    public async Task MqttCannotCombineCertificateWithPassword()
    {
        using var key = ECDsa.Create();
        using var certificate = ConnectionTrustTests.Certificate(key, "client", clientOnly: true);
        using var material = TlsClientCertificate.FromPem(certificate.ExportCertificatePem(), key.ExportPkcs8PrivateKeyPem(), DateTimeOffset.UtcNow);
        var connector = new NeverConnect();
        using var client = new MqttClientFactory().CreateMqttClient(new MqttBoundedAdapterFactory(() => { }, clientCertificate: material));
        var options = Options(connector, 12345).WithCredentials("fixture", "fixture-secret").Build();
        await Assert.ThrowsAsync<ArgumentException>(() => client.ConnectAsync(options, TestContext.Current.CancellationToken));
        Assert.Equal(0, connector.Attempts);
    }

    private static async Task ConnectAsync(bool http, IOutboundConnector connector, int port, TlsClientCertificate material, CancellationToken token)
    {
        if (http)
        {
            using var client = OutboundHttpTransport.CreateClient(connector, TimeSpan.FromSeconds(5), clientCertificate: material);
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://localhost:{port}/");
            using var response = await client.SendAsync(request, token);
        }
        else
        {
            using var client = new MqttClientFactory().CreateMqttClient(new MqttBoundedAdapterFactory(() => { }, clientCertificate: material));
            _ = await client.ConnectAsync(Options(connector, port).Build(), token);
        }
    }

    private static MqttClientOptionsBuilder Options(IOutboundConnector connector, int port) => new MqttClientOptionsBuilder()
        .WithTcpServer("localhost", port).WithStreamProvider(new MqttStreamProvider(connector)).WithProtocolVersion(MqttProtocolVersion.V311)
        .WithTlsOptions(tls => tls.UseTls().WithCertificateValidationHandler(args =>
            ServerCertificateTrust.Evaluate(args.Certificate, args.Chain, args.SslPolicyErrors, null) is null));

    private static bool HasCause<T>(Exception exception) where T : Exception =>
        exception is T || exception.InnerException is { } inner && HasCause<T>(inner);

    private sealed class DenyAll : IOutboundAddressPolicy
    {
        public bool IsAllowed(IPAddress address, int port) => false;
    }

    private static async Task<byte[]> ReadFrameAsync(SslStream stream, byte expected, CancellationToken token)
    {
        var header = new byte[2];
        await stream.ReadExactlyAsync(header, token);
        Assert.Equal(expected, header[0]);
        Assert.True(header[1] < 128, "夹具只使用小报文，不能误解析多字节 Remaining Length。");
        var payload = new byte[header[1]];
        await stream.ReadExactlyAsync(payload, token);
        return payload;
    }

    private sealed class CountingConnector(IOutboundConnector inner, Action connected) : IOutboundConnector
    {
        public ValueTask<Stream> ConnectAsync(string host, int port, CancellationToken cancellationToken)
        { connected(); return inner.ConnectAsync(host, port, cancellationToken); }
    }

    private sealed class NeverConnect : IOutboundConnector
    {
        public int Attempts { get; private set; }
        public ValueTask<Stream> ConnectAsync(string host, int port, CancellationToken cancellationToken)
        { Attempts++; throw new InvalidOperationException("不得访问网络。"); }
    }
}
