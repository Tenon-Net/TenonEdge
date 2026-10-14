using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Host.Security;
using TenonEdge.IntegrationTests.Infrastructure;

namespace TenonEdge.IntegrationTests.Processes;

public sealed partial class ApiTokenProcessTests
{
    private const string Root = "/api/edge-admin/v1/tokens";
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Theory]
    [Trait("Category", "Process")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealTlsTokensRequireTrustedCertificatesRespectProxyProtocolAndNeverPersistPlaintext(bool trustedProxy)
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "真实Host进程与来源地址测试按Linux执行。");
        using var data = TemporaryDirectory.Create();
        using var working = TemporaryDirectory.Create();
        byte[] publicCertificate;
        // Obtain only the public certificate via the local, owned data directory before the process starts.
        // This is an out-of-band trust decision, never trust-on-first-network-contact or a permissive callback.
        await using (var seed = new FixedDataDirectoryApplicationFactory(data.Path))
        { publicCertificate = seed.Services.GetRequiredService<HttpsCertificateStore>().Certificate.Export(X509ContentType.Cert); }
        using var certificate = X509CertificateLoader.LoadCertificate(publicCertificate);
        await using var host = HostProcess.Start(working.Path, "Production", data.Path,
            trustedProxy ? [$"--{TrustedProxies.ConfigurationKey}:0", "127.0.0.1"] : [],
            new Dictionary<string, string> { ["ASPNETCORE_FORWARDEDHEADERS_ENABLED"] = "true" });
        var http = await host.WaitForListeningAddressAsync(Timeout, Token);
        var https = await host.WaitForHttpsAddressAsync(Timeout, Token);
        using var health = new HttpClient { BaseAddress = http };
        await host.WaitUntilReadyAsync(health, Timeout, Token);
        using var admin = Client(https, certificate);
        if (trustedProxy)
        { admin.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https"); }
        using var setup = await admin.CompleteSetupAsync(Token);
        Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
        await admin.UseSessionAsync(setup, Token);
        using var created = await admin.PostJsonAsync(Root, new { name = "real TLS automation", role = "administrator" }, Token);
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var body = await created.ReadJsonAsync(Token);
        var secret = body.GetProperty("secret").GetString()!;
        var id = body.GetProperty("token").GetProperty("id").GetInt64();
        using var script = Client(https, certificate);
        script.DefaultRequestHeaders.Authorization = new("Bearer", secret);
        script.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        using var read = await script.GetAsync(Root, Token);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.False(read.Headers.Contains("Set-Cookie"));
        Assert.DoesNotContain(secret, await read.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);
        using var child = await script.PostJsonAsync(Root, new { name = "TLS child", role = "viewer" }, Token);
        Assert.Equal(HttpStatusCode.OK, child.StatusCode);
        var childSecret = (await child.ReadJsonAsync(Token)).GetProperty("secret").GetString()!;

        using var export = await script.PostJsonAsync("/api/edge-admin/v1/audit/export", new { filter = new { action = "token.create" }, confirmed = true }, Token);
        Assert.Equal(HttpStatusCode.Accepted, export.StatusCode);
        var csvReady = await Audit.AuditExportApiTests.WaitAsync(script, "ready");
        Assert.Equal("2", csvReady.GetProperty("count").GetString());
        var csvPath = $"/api/edge-admin/v1/audit/export/{csvReady.GetProperty("id").GetGuid()}/download";
        using var notOwner = await admin.GetAsync(csvPath, Token);
        Assert.Equal(HttpStatusCode.NotFound, notOwner.StatusCode);
        using var csvFile = await script.GetAsync(csvPath, Token);
        Assert.Equal(HttpStatusCode.OK, csvFile.StatusCode);
        Assert.Equal("text/csv", csvFile.Content.Headers.ContentType!.MediaType);
        var csv = await csvFile.Content.ReadAsStringAsync(Token);
        Assert.Contains("real TLS automation", csv, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, csv, StringComparison.Ordinal);
        var exportAudit = await admin.GetJsonAsync("/api/edge-admin/v1/audit?action=audit.export", Token);
        Assert.Equal("real TLS automation", Assert.Single(exportAudit.GetProperty("items").EnumerateArray()).GetProperty("tokenName").GetString());

        // A different trusted root cannot validate this Host. Default certificate name and chain validation stay enabled.
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=unrelated test root", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var wrongRoot = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        using var wrongTrust = Client(https, wrongRoot);
        await Assert.ThrowsAsync<HttpRequestException>(() => wrongTrust.GetAsync("/healthz", Token));

        using var plain = Client(http, certificate);
        plain.DefaultRequestHeaders.Authorization = new("Bearer", secret);
        using var rejected = await plain.GetAsync(Root, Token);
        Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);
        Assert.Equal("token.httpsRequired", await rejected.ReadProblemCodeAsync(Token));
        Assert.Null(rejected.Headers.Location);
        plain.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        using var forwarded = await plain.GetAsync(Root, Token);
        Assert.Equal(trustedProxy ? HttpStatusCode.OK : HttpStatusCode.Forbidden, forwarded.StatusCode);

        // A registered proxy without the forwarded protocol is HTTP, even when its upstream uses TLS.
        script.DefaultRequestHeaders.Remove("X-Forwarded-Proto");
        using var withoutProtocol = await script.GetAsync(Root, Token);
        Assert.Equal(trustedProxy ? HttpStatusCode.Forbidden : HttpStatusCode.OK, withoutProtocol.StatusCode);
        script.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");

        // Cookie over HTTP cannot create a token, including when an unregistered peer forges HTTPS.
        plain.DefaultRequestHeaders.Remove("Authorization");
        plain.DefaultRequestHeaders.Remove("X-Forwarded-Proto");
        using var login = await plain.LoginAsync("admin", ApiRequests.StrongPassword, Token);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await plain.UseSessionAsync(login, Token);
        if (!trustedProxy)
        { plain.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https"); }
        using var plainCreate = await plain.PostJsonAsync(Root, new { name = "http-must-not-exist", role = "administrator" }, Token);
        Assert.Equal(HttpStatusCode.Forbidden, plainCreate.StatusCode);
        Assert.Equal("token.httpsRequired", await plainCreate.ReadProblemCodeAsync(Token));

        using var revoked = await admin.PostJsonAsync($"{Root}/{id}/revoke", new { confirmed = true }, Token);
        Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
        using var ended = await script.GetAsync(Root, Token);
        Assert.Equal(HttpStatusCode.Unauthorized, ended.StatusCode);
        var audits = await admin.GetJsonAsync("/api/edge-admin/v1/audit?action=token.create", Token);
        Assert.Equal(2, audits.GetProperty("items").GetArrayLength());
        Assert.Contains(audits.GetProperty("items").EnumerateArray(), row => row.GetProperty("tokenName").GetString() == "real TLS automation");
        await host.RequestStopAsync(Token);
        Assert.Equal(0, await host.WaitForExitAsync(Timeout, Token));
        Assert.DoesNotContain(secret, host.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(childSecret, host.Output, StringComparison.Ordinal);
        foreach (var file in Directory.EnumerateFiles(data.Path, "*", SearchOption.AllDirectories))
        {
            var bytes = await File.ReadAllBytesAsync(file, Token);
            Assert.False(bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(secret)) >= 0, "Token plaintext persisted in " + Path.GetFileName(file));
            Assert.False(bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(childSecret)) >= 0, "Child token plaintext persisted in " + Path.GetFileName(file));
        }
    }

    [Theory]
    [Trait("Category", "Process")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AuthenticationFailuresUseOnlyTheTrustedSourceAddressAndDoNotLimitCookieLogin(bool trustedProxy)
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "来源地址绑定和真实进程按Linux执行。");
        using var data = TemporaryDirectory.Create();
        using var working = TemporaryDirectory.Create();
        byte[] publicCertificate;
        await using (var seed = new FixedDataDirectoryApplicationFactory(data.Path))
        { publicCertificate = seed.Services.GetRequiredService<HttpsCertificateStore>().Certificate.Export(X509ContentType.Cert); }
        using var certificate = X509CertificateLoader.LoadCertificate(publicCertificate);
        await using var host = HostProcess.Start(working.Path, "Production", data.Path,
            trustedProxy ? [$"--{TrustedProxies.ConfigurationKey}:0", "127.0.0.1"] : []);
        var https = await host.WaitForHttpsAddressAsync(Timeout, Token);
        using var admin = Client(https, certificate);
        admin.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        using var setup = await admin.CompleteSetupAsync(Token);
        Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
        using var attacker = Client(https, certificate);
        attacker.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        attacker.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "invalid-do-not-log");
        for (var i = 0; i < 11; i++)
        {
            attacker.DefaultRequestHeaders.Remove("X-Forwarded-For");
            attacker.DefaultRequestHeaders.Add("X-Forwarded-For", trustedProxy ? "203.0.113.9" : $"203.0.113.{i + 1}");
            using var response = await attacker.GetAsync(Root, Token);
            Assert.Equal(i < 10 ? HttpStatusCode.Unauthorized : HttpStatusCode.TooManyRequests, response.StatusCode);
            if (i == 10)
            { Assert.NotNull(response.Headers.RetryAfter); }
        }
        using var other = Client(https, certificate, trustedProxy ? null : IPAddress.Parse("127.0.0.2"));
        other.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        other.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.10");
        other.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "invalid-do-not-log");
        using var freshSource = await other.GetAsync(Root, Token);
        Assert.Equal(HttpStatusCode.Unauthorized, freshSource.StatusCode);
        using var login = await admin.LoginAsync("admin", ApiRequests.StrongPassword, Token);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await host.RequestStopAsync(Token);
        Assert.Equal(0, await host.WaitForExitAsync(Timeout, Token));
        Assert.DoesNotContain("invalid-do-not-log", host.Output, StringComparison.Ordinal);
    }

    private static HttpClient Client(Uri address, X509Certificate2 trustedCertificate, IPAddress? source = null)
    {
        var policy = new X509ChainPolicy { TrustMode = X509ChainTrustMode.CustomRootTrust, RevocationMode = X509RevocationMode.NoCheck };
        policy.CustomTrustStore.Add(trustedCertificate);
        policy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            SslOptions = new SslClientAuthenticationOptions { CertificateChainPolicy = policy }
        };
        if (source is not null)
        {
            handler.ConnectCallback = async (context, token) =>
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    socket.Bind(new IPEndPoint(source, 0));
                    await socket.ConnectAsync(context.DnsEndPoint, token);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch { socket.Dispose(); throw; }
            };
        }
        return new HttpClient(handler) { BaseAddress = address, Timeout = Timeout };
    }
}
