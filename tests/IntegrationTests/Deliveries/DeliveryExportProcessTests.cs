using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Host.Features.Audit;
using TenonEdge.Host.Features.Deliveries;
using TenonEdge.Host.Security;
using TenonEdge.IntegrationTests.Audit;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.Runtime.Deliveries;

namespace TenonEdge.IntegrationTests.Deliveries;

public sealed class DeliveryExportProcessTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnedSpoolIsRemovedOnGracefulStopOrNextStartupAfterRealKill(bool kill)
    {
        using var data = TemporaryDirectory.Create();
        using var work = TemporaryDirectory.Create();
        await using (var seed = new FixedDataDirectoryApplicationFactory(data.Path))
        {
            TestAccounts.Create(seed.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
            await DeliveryExportApiTests.SeedAsync(seed.Services.GetRequiredService<DeliveryCache>(), DeliveryState.Quarantined);
        }
        var unknown = Path.Combine(data.Path, DeliveryExportFiles.DirectoryName, "unknown.txt");
        await File.WriteAllTextAsync(unknown, "leave-unknown-file", Token);
        var path = DeliveryExportApiTests.FilePath(data.Path);
        var root = DeliveryExportApiTests.Root;
        Guid oldId;
        var auditPath = Path.Combine(data.Path, AuditExportFiles.DirectoryName, AuditExportFiles.FileName);
        await using (var process = HostProcess.Start(work.Path, "Production", data.Path))
        {
            using var client = new HttpClient { BaseAddress = await process.WaitForListeningAddressAsync(Timeout, Token) };
            await process.WaitUntilReadyAsync(client, Timeout, Token);
            using var login = await client.LoginAsync("admin", ApiRequests.StrongPassword, Token);
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            await client.UseSessionAsync(login, Token);
            using var prepared = await client.PostJsonAsync(root, new { state = "quarantined" }, Token);
            Assert.Equal(HttpStatusCode.Accepted, prepared.StatusCode);
            oldId = (await DeliveryExportApiTests.WaitAsync(client, "awaitingConfirmation")).GetProperty("id").GetGuid();
            using var confirmed = await client.PostJsonAsync($"{root}/{oldId}/confirm", new { confirmed = true }, Token);
            Assert.Equal(HttpStatusCode.Accepted, confirmed.StatusCode);
            await DeliveryExportApiTests.WaitAsync(client, "ready");
            using var auditStart = await client.PostJsonAsync(AuditExportApiTests.Root, new { filter = new { }, confirmed = true }, Token);
            Assert.Equal(HttpStatusCode.Accepted, auditStart.StatusCode);
            await AuditExportApiTests.WaitAsync(client, "ready");
            Assert.True(File.Exists(auditPath));
            Assert.True(File.Exists(path));
            if (kill)
            { await process.KillAsync(Token); }
            else
            {
                await process.RequestStopAsync(Token);
                Assert.Equal(0, await process.WaitForExitAsync(Timeout, Token));
            }
        }
        // 不依赖未经验证的 DeleteOnClose 强杀语义：强杀会留下文件，持锁的下次启动明确清理。
        Assert.Equal(kill, File.Exists(path));
        Assert.Equal(kill, File.Exists(auditPath));
        await using (var restarted = HostProcess.Start(work.Path, "Production", data.Path))
        {
            using var client = new HttpClient { BaseAddress = await restarted.WaitForListeningAddressAsync(Timeout, Token) };
            await restarted.WaitUntilReadyAsync(client, Timeout, Token);
            Assert.False(File.Exists(path));
            Assert.False(File.Exists(auditPath));
            Assert.Equal("leave-unknown-file", await File.ReadAllTextAsync(unknown, Token));
            using var login = await client.LoginAsync("admin", ApiRequests.StrongPassword, Token);
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            await client.UseSessionAsync(login, Token);
            Assert.Equal(JsonValueKind.Null, (await client.GetJsonAsync(root, Token)).GetProperty("export").ValueKind);
            using var old = await client.GetAsync(new Uri($"{root}/{oldId}/download", UriKind.Relative), Token);
            Assert.Equal(HttpStatusCode.NotFound, old.StatusCode);
            var records = await client.GetJsonAsync("/api/edge-admin/v1/deliveries?state=quarantined", Token);
            Assert.Single(records.GetProperty("items").EnumerateArray());
            await restarted.RequestStopAsync(Token);
            Assert.Equal(0, await restarted.WaitForExitAsync(Timeout, Token));
        }
        await using var inspect = new FixedDataDirectoryApplicationFactory(data.Path);
        Assert.Equal("success", Assert.Single(TestAccounts.Audit(inspect.Services, "delivery.export")).Result);
        Assert.Equal("success", Assert.Single(TestAccounts.Audit(inspect.Services, "audit.export")).Result);
    }
}
