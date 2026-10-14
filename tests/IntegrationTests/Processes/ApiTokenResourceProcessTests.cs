using System.Globalization;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Deliveries;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Features.SystemInfo;
using TenonEdge.Host.Security;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.IntegrationTests.Runtime;
using TenonEdge.Runtime.Deliveries;

namespace TenonEdge.IntegrationTests.Processes;

public sealed partial class ApiTokenProcessTests
{
    private const string Api = "/api/edge-admin/v1";

    [Fact]
    [Trait("Category", "Process")]
    public async Task RealTlsPreviewBudgetIsSharedByOneTokenButSeparateFromOtherTokensAndCreatorCookie()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "真实Host进程按Linux执行。");
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        await using var peer = builder.Build();
        peer.Urls.Add("http://127.0.0.1:0");
        var arrived = Channel.CreateUnbounded<bool>();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        peer.MapGet("/hold", async context =>
        {
            await arrived.Writer.WriteAsync(true, context.RequestAborted);
            await release.Task.WaitAsync(context.RequestAborted);
            context.Response.StatusCode = 204;
        });
        await peer.StartAsync(Token);
        var address = new Uri(peer.Urls.Single());
        using var data = TemporaryDirectory.Create();
        using var working = TemporaryDirectory.Create();
        byte[] publicCertificate;
        await using (var seed = new FixedDataDirectoryApplicationFactory(data.Path))
        {
            publicCertificate = seed.Services.GetRequiredService<HttpsCertificateStore>().Certificate.Export(X509ContentType.Cert);
            TestAccounts.Create(seed.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
            seed.Services.GetRequiredService<RuntimeParameterStore>().Write(new(new(180, 3, 1), null, true), "fixture", null);
        }
        using var certificate = X509CertificateLoader.LoadCertificate(publicCertificate);
        await using var host = HostProcess.Start(working.Path, "Production", data.Path,
            ["--TenonEdge:Outbound:LocalPorts:0", address.Port.ToString(CultureInfo.InvariantCulture)]);
        var https = await host.WaitForHttpsAddressAsync(Timeout, Token);
        using var admin = Client(https, certificate);
        using var login = await admin.LoginAsync("admin", ApiRequests.StrongPassword, Token);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await admin.UseSessionAsync(login, Token);
        var first = await CreateToken(admin, "preview A", "configurator");
        var second = await CreateToken(admin, "preview B", "configurator");
        var readOnly = await CreateToken(admin, "preview read only", "viewer");
        using var owner = Bearer(https, certificate, first.Secret);
        using var sameToken = Bearer(https, certificate, first.Secret);
        using var other = Bearer(https, certificate, second.Secret);
        using var viewer = Bearer(https, certificate, readOnly.Secret);
        var probe = new HttpConnectionTestRequest(new(new Uri(address, "/hold").AbsoluteUri, TimeoutMs: 30000));
        const string endpoint = Api + "/connections/test/http";
        var held = new List<Task<HttpResponseMessage>>();
        try
        {
            held.Add(owner.PostJsonAsync(endpoint, probe, Token));
            await arrived.Reader.ReadAsync(Token).AsTask().WaitAsync(Timeout, Token);
            using var sameBudget = await sameToken.PostJsonAsync(Api + "/connections/inspect-outbound", new { host = "203.0.113.1", port = 443 }, Token);
            Assert.Equal(HttpStatusCode.TooManyRequests, sameBudget.StatusCode);
            Assert.Equal("preview.busy", await sameBudget.ReadProblemCodeAsync(Token));
            held.Add(other.PostJsonAsync(endpoint, probe, Token));
            await arrived.Reader.ReadAsync(Token).AsTask().WaitAsync(Timeout, Token);
            held.Add(admin.PostJsonAsync(endpoint, probe, Token));
            await arrived.Reader.ReadAsync(Token).AsTask().WaitAsync(Timeout, Token);
            using var forbidden = await viewer.PostJsonAsync(endpoint, probe, Token);
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
            // A new token from the same creator has its own budget, but the global limit still applies.
            var third = await CreateToken(admin, "preview C", "configurator");
            using var global = Bearer(https, certificate, third.Secret);
            using var globalBusy = await global.PostJsonAsync(endpoint, probe, Token);
            Assert.Equal(HttpStatusCode.TooManyRequests, globalBusy.StatusCode);
            using var revoke = await admin.PostJsonAsync($"{Root}/{first.Id}/revoke", new { confirmed = true }, Token);
            Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
            using var revoked = await sameToken.PostJsonAsync(endpoint, probe, Token);
            Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
            release.TrySetResult();
            foreach (var pending in held)
            {
                using var response = await pending.WaitAsync(Timeout, Token);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var result = await response.ReadJsonAsync(Token);
                Assert.Equal(204, result.GetProperty("statusCode").GetInt32());
            }
            held.Clear();
            using var afterRelease = await global.PostJsonAsync(endpoint, probe, Token);
            Assert.Equal(HttpStatusCode.OK, afterRelease.StatusCode);
            Assert.Empty((await admin.GetJsonAsync(Api + "/connections", Token)).GetProperty("connections").EnumerateArray());
        }
        finally
        {
            release.TrySetResult();
            foreach (var pending in held)
            { using var response = await pending.WaitAsync(Timeout, Token); }
            await peer.StopAsync(Token);
        }
        await host.RequestStopAsync(Token);
        Assert.Equal(0, await host.WaitForExitAsync(Timeout, Token));
        Assert.DoesNotContain(first.Secret, host.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(second.Secret, host.Output, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Process")]
    public async Task RealTlsMigrationPreparationBelongsToTokenNotCreatorAndConfirmedOperationKeepsTokenAudit()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "真实Host进程按Linux执行。");
        using var data = TemporaryDirectory.Create();
        using var working = TemporaryDirectory.Create();
        byte[] publicCertificate;
        long sourceId, targetId;
        var deliveryId = Guid.NewGuid();
        await using (var seed = new FixedDataDirectoryApplicationFactory(data.Path))
        {
            publicCertificate = seed.Services.GetRequiredService<HttpsCertificateStore>().Certificate.Export(X509ContentType.Cert);
            TestAccounts.Create(seed.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
            var connections = seed.Services.GetRequiredService<ConnectionDraftStore>();
            var source = connections.Write(null, new("0", "source", "Source", ConnectionKind.Http, Http: new("http://source.test/")), "fixture", null);
            var target = connections.Write(null, new(source.Version, "target", "Target", ConnectionKind.Http, Http: new("http://target.test/")), "fixture", null);
            sourceId = source.Connection.Id;
            targetId = target.Connection.Id;
            var controls = seed.Services.GetRequiredService<OutputControlStore>();
            controls.Write(sourceId, new(true, true), "fixture", null);
            controls.Write(targetId, new(true, true), "fixture", null);
            var coordinator = seed.Services.GetRequiredService<RuntimeCoordinator>();
            Assert.True((await coordinator.ApplyDraftAsync(target.Version, "fixture", null, Token)).Activated);
            await OutputPipelineTests.UntilAsync(() => coordinator.Runtime.GetOutputSenders() is { Length: 2 } senders &&
                senders.All(s => s.Sender.State == "manuallyPaused"));
            var cache = seed.Services.GetRequiredService<DeliveryCache>();
            var queued = await cache.EnqueueAsync([new(deliveryId, sourceId, Guid.NewGuid(), "meter", 1, "{\"frozen\":42}"u8.ToArray(),
                "old-fingerprint", OutputRecipientIdentity.Http("http://original.test/", null).Freeze(), "original/path")], Token);
            Assert.Single(queued.Seqs);
            // Fixed fixture state, not a claim that the original target has been contacted.
            await cache.QuarantineAsync(queued.Seqs[0], DeliveryQuarantineReasons.TargetChanged, Token);
        }
        using var certificate = X509CertificateLoader.LoadCertificate(publicCertificate);
        await using var host = HostProcess.Start(working.Path, "Production", data.Path);
        var https = await host.WaitForHttpsAddressAsync(Timeout, Token);
        using var admin = Client(https, certificate);
        using var login = await admin.LoginAsync("admin", ApiRequests.StrongPassword, Token);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await admin.UseSessionAsync(login, Token);
        var first = await CreateToken(admin, "migration A", "administrator");
        var second = await CreateToken(admin, "migration B", "administrator");
        var config = await CreateToken(admin, "migration config", "configurator");
        var readOnly = await CreateToken(admin, "migration viewer", "viewer");
        using var owner = Bearer(https, certificate, first.Secret);
        using var sameToken = Bearer(https, certificate, first.Secret);
        using var other = Bearer(https, certificate, second.Secret);
        const string migration = Api + "/deliveries/migrations";
        var page = await owner.GetJsonAsync($"{migration}/records?connectionId={sourceId}&state=quarantined", Token);
        var item = Assert.Single(page.GetProperty("items").EnumerateArray());
        var request = new DeliveryMigrationPrepareRequest(sourceId, targetId,
            [new(item.GetProperty("sequence").GetInt64(), item.GetProperty("deliveryId").GetGuid(), null)]);
        using var prepare = await owner.PostJsonAsync(migration + "/prepare", request, Token);
        Assert.Equal(HttpStatusCode.OK, prepare.StatusCode);
        var id = (await prepare.ReadJsonAsync(Token)).GetProperty("id").GetGuid();
        foreach (var deniedOwner in new[] { admin, other })
        {
            Assert.Equal(JsonValueKind.Null, (await deniedOwner.GetJsonAsync(migration + "/preparation", Token)).GetProperty("preparation").ValueKind);
            using var stolen = await deniedOwner.PostJsonAsync($"{migration}/{id}/confirm", new { sameOwnerConfirmed = true }, Token);
            Assert.Equal(HttpStatusCode.NotFound, stolen.StatusCode);
            using var cancel = await deniedOwner.DeleteAsync($"{migration}/preparation/{id}", Token);
            Assert.Equal(HttpStatusCode.NotFound, cancel.StatusCode);
        }
        foreach (var secret in new[] { config.Secret, readOnly.Secret })
        {
            using var role = Bearer(https, certificate, secret);
            using var denied = await role.PostJsonAsync(migration + "/prepare", request, Token);
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }
        Assert.Equal(id, (await sameToken.GetJsonAsync(migration + "/preparation", Token)).GetProperty("preparation").GetProperty("id").GetGuid());
        using var confirm = await sameToken.PostJsonAsync($"{migration}/{id}/confirm", new { sameOwnerConfirmed = true }, Token);
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);
        Assert.Equal(1, (await confirm.ReadJsonAsync(Token)).GetProperty("migratedRecords").GetInt32());
        using var replay = await owner.PostJsonAsync($"{migration}/{id}/confirm", new { sameOwnerConfirmed = true }, Token);
        Assert.Equal(HttpStatusCode.NotFound, replay.StatusCode);
        Assert.Equal("completed", (await owner.GetJsonAsync($"{migration}/{id}", Token)).GetProperty("status").GetString());
        var moved = await owner.GetJsonAsync($"{migration}/records?connectionId={targetId}&state=pending", Token);
        Assert.Equal(deliveryId, Assert.Single(moved.GetProperty("items").EnumerateArray()).GetProperty("deliveryId").GetGuid());
        foreach (var action in new[] { "delivery.migrationStarted", "delivery.migrationFinished" })
        {
            var audit = await admin.GetJsonAsync(Api + "/audit?action=" + action, Token);
            Assert.Equal("migration A", Assert.Single(audit.GetProperty("items").EnumerateArray()).GetProperty("tokenName").GetString());
        }
        using var revoked = await admin.PostJsonAsync($"{Root}/{first.Id}/revoke", new { confirmed = true }, Token);
        Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
        using var noLongerOwner = await owner.GetAsync($"{migration}/{id}", Token);
        Assert.Equal(HttpStatusCode.Unauthorized, noLongerOwner.StatusCode);
        // Revocation ends subsequent access, not a committed migration or its durable result.
        Assert.Equal("completed", (await other.GetJsonAsync($"{migration}/{id}", Token)).GetProperty("status").GetString());
        await host.RequestStopAsync(Token);
        Assert.Equal(0, await host.WaitForExitAsync(Timeout, Token));
        Assert.DoesNotContain(first.Secret, host.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(second.Secret, host.Output, StringComparison.Ordinal);
    }

    private static HttpClient Bearer(Uri address, X509Certificate2 certificate, string secret)
    {
        var client = Client(address, certificate);
        client.DefaultRequestHeaders.Authorization = new("Bearer", secret);
        Assert.False(client.DefaultRequestHeaders.Contains(ApiRequests.CsrfHeader));
        return client;
    }
    private static async Task<(long Id, string Secret)> CreateToken(HttpClient admin, string name, string role)
    {
        using var response = await admin.PostJsonAsync(Root, new { name, role }, Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.ReadJsonAsync(Token);
        return (body.GetProperty("token").GetProperty("id").GetInt64(), body.GetProperty("secret").GetString()!);
    }
}
