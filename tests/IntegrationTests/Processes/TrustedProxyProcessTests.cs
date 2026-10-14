using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Host.Features.SystemInfo;
using TenonEdge.Host.Security;
using TenonEdge.IntegrationTests.Infrastructure;

namespace TenonEdge.IntegrationTests.Processes;

public sealed partial class ApiTokenProcessTests
{
    [Fact]
    [Trait("Category", "Process")]
    public async Task PersistedProxyTrustChangesActualProtocolAndSourceWithoutRestartAndCannotRemoveDeploymentTrust()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "真实Host与来源地址绑定按Linux执行。");
        const string endpoint = Api + "/system/trusted-proxies";
        using var data = TemporaryDirectory.Create();
        using var working = TemporaryDirectory.Create();
        byte[] publicCertificate;
        await using (var fixture = new FixedDataDirectoryApplicationFactory(data.Path))
        {
            publicCertificate = fixture.Services.GetRequiredService<HttpsCertificateStore>().Certificate.Export(X509ContentType.Cert);
            TestAccounts.Create(fixture.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        }
        using var certificate = X509CertificateLoader.LoadCertificate(publicCertificate);
        var args = new[] { "--" + TrustedProxies.ConfigurationKey + ":0", "127.0.0.2" };
        string secret;
        string version;
        await using (var host = HostProcess.Start(working.Path, "Production", data.Path, args))
        {
            var https = await host.WaitForHttpsAddressAsync(Timeout, Token);
            var http = await host.WaitForListeningAddressAsync(Timeout, Token);
            using var admin = Client(https, certificate);
            using var login = await admin.LoginAsync("admin", ApiRequests.StrongPassword, Token);
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            await admin.UseSessionAsync(login, Token);
            secret = (await CreateToken(admin, "proxy automation", "administrator")).Secret;
            using var script = Bearer(http, certificate, secret);
            script.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
            script.DefaultRequestHeaders.Add("X-Forwarded-For", "198.51.100.23");
            using var notTrusted = await script.GetAsync(endpoint, Token);
            Assert.Equal(HttpStatusCode.Forbidden, notTrusted.StatusCode);
            Assert.Equal("token.httpsRequired", await notTrusted.ReadProblemCodeAsync(Token));
            using var save = await admin.PutAsJsonAsync(endpoint, new TrustedProxyRequest(["127.0.0.1"], null, true), Token);
            Assert.Equal(HttpStatusCode.OK, save.StatusCode);
            version = (await save.ReadJsonAsync(Token)).GetProperty("version").GetString()!;
            using var nowTrusted = await script.GetAsync(endpoint, Token);
            Assert.Equal(HttpStatusCode.OK, nowTrusted.StatusCode);
            using var missingProtocol = await admin.GetAsync(endpoint, Token);
            Assert.Equal(HttpStatusCode.Unauthorized, missingProtocol.StatusCode); // Registered proxy without protocol is HTTP, even on TLS.
            admin.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
            using var recovered = await admin.GetAsync(endpoint, Token);
            Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
            var child = await CreateToken(script, "forwarded provenance", "viewer");
            var audit = await admin.GetJsonAsync(Api + "/audit?action=token.create", Token);
            var entry = Assert.Single(audit.GetProperty("items").EnumerateArray(), row => row.GetProperty("tokenName").GetString() == "proxy automation");
            Assert.Equal("198.51.100.23", entry.GetProperty("sourceIp").GetString());
            using var tlsProtocolMismatch = Bearer(https, certificate, secret);
            tlsProtocolMismatch.DefaultRequestHeaders.Add("X-Forwarded-Proto", "http");
            using var notHttps = await tlsProtocolMismatch.GetAsync(endpoint, Token);
            Assert.Equal(HttpStatusCode.Forbidden, notHttps.StatusCode);
            await host.RequestStopAsync(Token);
            Assert.Equal(0, await host.WaitForExitAsync(Timeout, Token));
            Assert.DoesNotContain(secret, host.Output, StringComparison.Ordinal);
            Assert.DoesNotContain(child.Secret, host.Output, StringComparison.Ordinal);
        }
        await using (var restarted = HostProcess.Start(working.Path, "Production", data.Path, args))
        {
            var https = await restarted.WaitForHttpsAddressAsync(Timeout, Token);
            var http = await restarted.WaitForListeningAddressAsync(Timeout, Token);
            using var admin = Bearer(https, certificate, secret);
            admin.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
            var persisted = (await admin.GetFromJsonAsync<TrustedProxyState>(endpoint, Token))!;
            Assert.Equal(version, persisted.Version);
            Assert.Equal(["127.0.0.1/32"], persisted.Networks);
            Assert.Equal(["127.0.0.2/32"], persisted.Deployment);
            using var clear = await admin.PutAsJsonAsync(endpoint, new TrustedProxyRequest([], version, true), Token);
            Assert.Equal(HttpStatusCode.OK, clear.StatusCode);
            using var removed = Bearer(http, certificate, secret);
            removed.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
            using var deniedAgain = await removed.GetAsync(endpoint, Token);
            Assert.Equal(HttpStatusCode.Forbidden, deniedAgain.StatusCode);
            using var deployment = Client(http, certificate, IPAddress.Parse("127.0.0.2"));
            deployment.DefaultRequestHeaders.Authorization = new("Bearer", secret);
            deployment.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
            using var stillTrusted = await deployment.GetAsync(endpoint, Token);
            Assert.Equal(HttpStatusCode.OK, stillTrusted.StatusCode);
            // The real TLS connection is authoritative again for the removed proxy, even with a spoofed header.
            admin.DefaultRequestHeaders.Remove("X-Forwarded-Proto");
            admin.DefaultRequestHeaders.Add("X-Forwarded-Proto", "http");
            using var directTls = await admin.GetAsync(endpoint, Token);
            Assert.Equal(HttpStatusCode.OK, directTls.StatusCode);
            await restarted.RequestStopAsync(Token);
            Assert.Equal(0, await restarted.WaitForExitAsync(Timeout, Token));
            Assert.DoesNotContain(secret, restarted.Output, StringComparison.Ordinal);
        }
    }
}
