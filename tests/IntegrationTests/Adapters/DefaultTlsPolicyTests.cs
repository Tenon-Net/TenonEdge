using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using TenonEdge.IntegrationTests.Infrastructure;

namespace TenonEdge.IntegrationTests.Adapters;

/// <summary>原产品 Host 子进程使用独立 OpenSSL 根目录，不修改系统/当前用户证书库。
/// 不配置连接信任，真实握手验证默认系统信任及证书网址零外联。</summary>
public sealed class DefaultTlsPolicyTests
{
    public static TheoryData<bool, string, string?> Cases => new()
    {
        { true, "system", null }, { false, "system", null },
        { true, "revocation", null }, { false, "revocation", null },
        { true, "missingIssuer", "certificate.untrusted" }, { false, "missingIssuer", "certificate.untrusted" },
        { true, "untrusted", "certificate.untrusted" }, { false, "untrusted", "certificate.untrusted" },
        { true, "name", "certificate.nameMismatch" }, { false, "name", "certificate.nameMismatch" },
        { true, "expired", "certificate.expired" }, { false, "expired", "certificate.expired" },
        { true, "future", "certificate.notYetValid" }, { false, "future", "certificate.notYetValid" },
        { true, "usage", "certificate.invalid" }, { false, "usage", "certificate.invalid" },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task DefaultTrustUsesLocalRootsWithoutFetchingCertificateUrls(bool http, string scenario, string? expected)
    {
        // SSL_CERT_FILE/DIR are OpenSSL-specific. Do not claim a Windows trust-store test.
        Assert.True(OperatingSystem.IsLinux(), "此测试须在支持的 Linux/OpenSSL 环境执行。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        var token = timeout.Token;
        using var directory = TemporaryDirectory.Create();
        using var urls = new TcpListener(IPAddress.Loopback, 0);
        urls.Start();
        var certificateUrl = $"http://127.0.0.1:{((IPEndPoint)urls.LocalEndpoint).Port}/certificate-data";
        var downloads = 0;
        using var downloadStop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var monitor = MonitorAsync(downloadStop.Token);
        try
        {
            using var rootKey = ECDsa.Create();
            using var root = ConnectionTrustTests.Certificate(rootKey, "root-" + Guid.NewGuid().ToString("N"), authority: true);
            using var intermediateKey = ECDsa.Create();
            using var intermediate = ConnectionTrustTests.Certificate(intermediateKey, "intermediate-" + Guid.NewGuid().ToString("N"), authority: true, issuer: root);
            using var serverKey = ECDsa.Create();
            using var server = ConnectionTrustTests.Certificate(serverKey, scenario == "name" ? "wrong.invalid" : "localhost",
                issuer: scenario == "missingIssuer" ? intermediate : root, expired: scenario == "expired", future: scenario == "future",
                clientOnly: scenario == "usage", issuerUrl: scenario == "missingIssuer" ? certificateUrl : null,
                revocationUrl: scenario == "revocation" ? certificateUrl : null);
            var rootFile = Path.Combine(directory.Path, "roots.pem");
            await File.WriteAllTextAsync(rootFile, scenario == "untrusted" ? "" : root.ExportCertificatePem(), token);
            var emptyRoots = Directory.CreateDirectory(Path.Combine(directory.Path, "empty-roots")).FullName;
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var dataDirectory = Directory.CreateDirectory(Path.Combine(directory.Path, "data")).FullName;
            await using var host = HostProcess.Start(directory.Path, "Production", dataDirectory,
                [$"--TenonEdge:Outbound:LocalPorts:0={port}"], new Dictionary<string, string>
                {
                    ["SSL_CERT_FILE"] = rootFile,
                    ["SSL_CERT_DIR"] = emptyRoots,
                    ["HOME"] = directory.Path,
                    ["HTTP_PROXY"] = "",
                    ["HTTPS_PROXY"] = "",
                    ["ALL_PROXY"] = "",
                });
            var address = await host.WaitForListeningAddressAsync(TimeSpan.FromSeconds(20), token);
            await host.WaitForApplicationStartedAsync(TimeSpan.FromSeconds(20), token);
            using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = address };
            using var setup = await client.CompleteSetupAsync(token);
            Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
            await client.UseSessionAsync(setup, token);
            var appBytes = 0;
            var peer = PeerAsync();
            object request = http
                ? new { connection = new { baseAddress = $"https://localhost:{port}/", timeoutMs = 5000 }, authentication = "provided", credential = new { kind = "bearer", secret = "fixture-secret" } }
                : new { connection = new { host = "localhost", port, tls = true, timeoutMs = 5000 }, authentication = "provided", username = "fixture", password = "fixture-secret" };
            using var response = await client.PostJsonAsync("/api/edge-admin/v1/connections/test/" + (http ? "http" : "mqtt"), request, token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var result = await response.ReadJsonAsync(token);
            await peer;
            Assert.Equal(0, Volatile.Read(ref downloads));
            Assert.Equal(expected is null, result.GetProperty("success").GetBoolean());
            Assert.Equal(expected ?? (http ? "http.response" : "success"), result.GetProperty("code").GetString());
            Assert.Equal(expected is null, appBytes > 0);
            Assert.Equal(server.GetCertHashString(HashAlgorithmName.SHA256), result.GetProperty("certificate").GetProperty("sha256Fingerprint").GetString());

            async Task PeerAsync()
            {
                using var accepted = await listener.AcceptTcpClientAsync(token);
                using var stream = new SslStream(accepted.GetStream());
                try
                {
                    // Deliberately omit the intermediate in missingIssuer; the client must not fetch it.
                    var chain = scenario == "missingIssuer" ? null : new X509Certificate2Collection(root);
                    await stream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                    { ServerCertificateContext = SslStreamCertificateContext.Create(server, chain, offline: true) }, token);
                    var buffer = new byte[4096];
                    appBytes = await stream.ReadAsync(buffer, token);
                    if (appBytes > 0)
                    {
                        await stream.WriteAsync(http ? "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray()
                            : new byte[] { 0x20, 2, 0, 0 }, token);
                        if (!http)
                        { _ = await stream.ReadAsync(buffer, token); }
                    }
                }
                catch (Exception exception) when (exception is AuthenticationException or IOException) { }
            }
        }
        finally
        {
            await downloadStop.CancelAsync();
            await monitor;
        }

        async Task MonitorAsync(CancellationToken stop)
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    using var connection = await urls.AcceptTcpClientAsync(stop);
                    Interlocked.Increment(ref downloads);
                    var stream = connection.GetStream();
                    _ = await stream.ReadAsync(new byte[8192], stop);
                    await stream.WriteAsync("HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(), stop);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }
    }
}
