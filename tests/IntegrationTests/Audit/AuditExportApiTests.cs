using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Host.Features.Audit;
using TenonEdge.Host.Security;
using TenonEdge.Host.Storage;
using TenonEdge.Host.Storage.Ids;
using TenonEdge.IntegrationTests.Infrastructure;

namespace TenonEdge.IntegrationTests.Audit;

public sealed class AuditExportApiTests
{
    internal const string Root = "/api/edge-admin/v1/audit/export";
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task CompleteCsvIsAdminSessionOwnedSingleUseAndPersistentlyAuditedBeforeReady()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        var ids = Seed(factory.Services, 405);
        using var admin = await factory.CreateLoggedInClientAsync("admin", Token);
        using var second = await factory.CreateLoggedInClientAsync("admin", Token);
        using var start = await admin.PostJsonAsync(Root, new { filter = new { action = "fixture.csv", account = "fixture", target = "device" }, confirmed = true }, Token);
        Assert.Equal(HttpStatusCode.Accepted, start.StatusCode);
        Assert.True(start.Headers.CacheControl!.NoStore);
        var id = (await start.ReadJsonAsync(Token)).GetProperty("id").GetGuid();
        var ready = await WaitAsync(admin, "ready");
        Assert.Equal("405", ready.GetProperty("count").GetString());
        Assert.True(ready.GetProperty("upperId").GetInt64() >= ids[^1]);
        Assert.Equal("success", Assert.Single(TestAccounts.Audit(factory.Services, "audit.export")).Result);
        factory.Services.GetRequiredService<AuditTrail>().AppendOrLog(new("fixture.csv", "success", "fixture", null, "device", "late"));
        Assert.Equal(JsonValueKind.Null, (await second.GetJsonAsync(Root, Token)).GetProperty("export").ValueKind);
        using var denied = await second.GetAsync($"{Root}/{id}/download", Token);
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        using var cancelDenied = await second.PostJsonAsync($"{Root}/{id}/cancel", new { }, Token);
        Assert.Equal(HttpStatusCode.NotFound, cancelDenied.StatusCode);
        using var repeat = await admin.PostJsonAsync(Root, new { filter = new { }, confirmed = true }, Token);
        Assert.Equal(HttpStatusCode.Conflict, repeat.StatusCode);
        using var download = await admin.GetAsync($"{Root}/{id}/download", Token);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.True(download.Headers.CacheControl!.NoStore);
        Assert.Equal("text/csv", download.Content.Headers.ContentType!.MediaType);
        Assert.Equal("attachment", download.Content.Headers.ContentDisposition!.DispositionType);
        var bytes = await download.Content.ReadAsByteArrayAsync(Token);
        Assert.Equal(bytes.Length, download.Content.Headers.ContentLength);
        Assert.Equal(new byte[] { 239, 187, 191 }, bytes[..3]);
        var csv = Encoding.UTF8.GetString(bytes);
        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(406, lines.Length);
        Assert.StartsWith("\"" + ids[^1] + "\",", lines[1], StringComparison.Ordinal);
        Assert.StartsWith("\"" + ids[0] + "\",", lines[^1], StringComparison.Ordinal);
        Assert.DoesNotContain("late", csv, StringComparison.Ordinal);
        Assert.DoesNotContain("audit.export", csv, StringComparison.Ordinal);
        await WaitAsync(admin, "responseWritten");
        Assert.Equal("success", Assert.Single(TestAccounts.Audit(factory.Services, "audit.export.download")).Result);
        using var again = await admin.GetAsync($"{Root}/{id}/download", Token);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    [Theory]
    [InlineData("Viewer")]
    [InlineData("Configurator")]
    public async Task OtherRolesCannotListExportReadOrCancel(string role)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "user", ApiRequests.StrongPassword, Enum.Parse<UserRole>(role));
        using var user = await factory.CreateLoggedInClientAsync("user", Token);
        foreach (var path in new[] { "/api/edge-admin/v1/audit", Root, $"{Root}/{Guid.NewGuid()}/download" })
        { using var denied = await user.GetAsync(path, Token); Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode); }
        foreach (var path in new[] { Root, $"{Root}/{Guid.NewGuid()}/cancel" })
        { using var denied = await user.PostJsonAsync(path, new { filter = new { }, confirmed = true }, Token); Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode); }
    }

    [Fact]
    public async Task InvalidRequestsAndMissingCsrfNeverStartAndEmptyScopeStillGeneratesAHeader()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        using var admin = await factory.CreateLoggedInClientAsync("admin", Token);
        foreach (var request in new object[] { new { filter = new { }, confirmed = false }, new { confirmed = true },
            new { filter = new { action = new string('x', 129) }, confirmed = true }, new { filter = new { before = 1 }, confirmed = true },
            new { filter = new { from = "2026-01-02", to = "2026-01-01" }, confirmed = true } })
        { using var bad = await admin.PostJsonAsync(Root, request, Token); Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode); }
        Assert.Null(factory.Services.GetRequiredService<AuditExportManager>().GetCurrent("unused"));
        using var noCsrf = await factory.CreateLoggedInClientAsync("admin", Token);
        noCsrf.DefaultRequestHeaders.Remove(ApiRequests.CsrfHeader);
        using var blocked = await noCsrf.PostJsonAsync(Root, new { filter = new { }, confirmed = true }, Token);
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
        using var anonymous = factory.CreateClient();
        using var unauthorized = await anonymous.GetAsync(Root, Token);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        using var start = await admin.PostJsonAsync(Root, new { filter = new { action = "never.exists" }, confirmed = true }, Token);
        Assert.Equal(HttpStatusCode.Accepted, start.StatusCode);
        var ready = await WaitAsync(admin, "ready");
        Assert.Equal("0", ready.GetProperty("count").GetString());
        using var file = await admin.GetAsync($"{Root}/{ready.GetProperty("id").GetGuid()}/download", Token);
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        Assert.Equal("id,occurredAt,accountName,tokenName,sourceIp,action,target,result,detail\r\n", await file.Content.ReadAsStringAsync(Token));
    }

    [Fact]
    public async Task BearerHasItsOwnOwnershipAndAuditsAndRevocationPreventsDownload()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        using var admin = factory.CreateClient(new() { BaseAddress = new("https://localhost") });
        using var setup = await admin.CompleteSetupAsync(Token);
        await admin.UseSessionAsync(setup, Token);
        using var create = await admin.PostJsonAsync("/api/edge-admin/v1/tokens", new { name = "CSV automation", role = "administrator" }, Token);
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        var created = await create.ReadJsonAsync(Token);
        using var script = factory.CreateClient(new() { BaseAddress = new("https://localhost"), HandleCookies = false });
        script.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", created.GetProperty("secret").GetString());
        using var start = await script.PostJsonAsync(Root, new { filter = new { }, confirmed = true }, Token);
        Assert.Equal(HttpStatusCode.Accepted, start.StatusCode);
        var ready = await WaitAsync(script, "ready");
        var id = ready.GetProperty("id").GetGuid();
        using var denied = await admin.GetAsync($"{Root}/{id}/download", Token);
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        Assert.Equal("CSV automation", Assert.Single(TestAccounts.Audit(factory.Services, "audit.export")).TokenName);
        using var revoke = await admin.PostJsonAsync($"/api/edge-admin/v1/tokens/{created.GetProperty("token").GetProperty("id").GetInt64()}/revoke", new { confirmed = true }, Token);
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        using var revoked = await script.GetAsync($"{Root}/{id}/download", Token);
        Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
    }

    internal static long[] Seed(IServiceProvider services, int count)
    {
        var ids = services.GetRequiredService<SnowflakeIdGenerator>();
        return services.GetRequiredService<ConfigDatabase>().InTransaction(client =>
        {
            var rows = Enumerable.Range(0, count).Select(i => new AuditLogRecord
            {
                Id = ids.NextId(),
                OccurredAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                AccountName = "fixture",
                Action = "fixture.csv",
                Target = "device",
                Result = "success",
                Detail = "row-" + i
            }).ToArray();
            client.Insertable(rows).ExecuteCommand();
            return rows.Select(row => row.Id).ToArray();
        });
    }
    internal static async Task<JsonElement> WaitAsync(HttpClient client, string state)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        while (true)
        {
            var status = (await client.GetJsonAsync(Root, deadline.Token)).GetProperty("export");
            if (status.GetProperty("state").GetString() == state)
            { return status; }
            Assert.NotEqual("failed", status.GetProperty("state").GetString());
            await Task.Delay(20, deadline.Token);
        }
    }
}
