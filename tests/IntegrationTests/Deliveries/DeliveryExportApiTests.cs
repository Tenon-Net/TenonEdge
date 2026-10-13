using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Host.Features.Deliveries;
using TenonEdge.Host.Security;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.Runtime.Deliveries;

namespace TenonEdge.IntegrationTests.Deliveries;

public sealed class DeliveryExportApiTests
{
    internal const string Root = "/api/edge-admin/v1/deliveries/export";
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task EveryEndpointRequiresAdminAndPostsRequireCsrf()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var paths = new[] { Root, Root + "/" + Guid.NewGuid() + "/confirm", Root + "/" + Guid.NewGuid() + "/cancel" };
        using var anonymous = factory.CreateClient();
        foreach (var path in paths)
        {
            using var denied = await anonymous.PostJsonAsync(path, new { state = "failed", confirmed = true }, Token);
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        }
        foreach (var role in new[] { UserRole.Configurator, UserRole.Viewer, UserRole.Administrator })
        {
            TestAccounts.Create(factory.Services, role.ToString(), ApiRequests.StrongPassword, role);
            using var client = await factory.CreateLoggedInClientAsync(role.ToString(), Token);
            if (role == UserRole.Administrator)
            { client.DefaultRequestHeaders.Remove(ApiRequests.CsrfHeader); }
            foreach (var path in paths)
            {
                using var denied = await client.PostJsonAsync(path, new { state = "failed", confirmed = true }, Token);
                Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            }
            if (role != UserRole.Administrator)
            {
                foreach (var path in new[] { Root, Root + "/" + Guid.NewGuid() + "/download" })
                {
                    using var denied = await client.GetAsync(new Uri(path, UriKind.Relative), Token);
                    Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
                }
            }
        }
        foreach (var path in new[] { Root, Root + "/" + Guid.NewGuid() + "/download" })
        {
            using var denied = await anonymous.GetAsync(new Uri(path, UriKind.Relative), Token);
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        }
    }

    [Theory]
    [InlineData(DeliveryState.Failed)]
    [InlineData(DeliveryState.Quarantined)]
    public async Task ConfirmedCompleteDownloadIsSessionOwnedAuditedAndSingleUse(DeliveryState area)
    {
        using var data = TemporaryDirectory.Create();
        await using var factory = new FixedDataDirectoryApplicationFactory(data.Path);
        TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        TestAccounts.Create(factory.Services, "another", ApiRequests.StrongPassword, UserRole.Administrator);
        var cache = factory.Services.GetRequiredService<DeliveryCache>();
        var body = Encoding.UTF8.GetBytes(new string('x', 256 * 1024 - 30) + "\n9007199254740993999😀");
        var draft = await SeedAsync(cache, area, body);
        using var admin = await factory.CreateLoggedInClientAsync("admin", Token);
        using var secondSession = await factory.CreateLoggedInClientAsync("admin", Token);
        using var another = await factory.CreateLoggedInClientAsync("another", Token);
        using var prepare = await admin.PostJsonAsync(Root, new { state = area.ToString().ToLowerInvariant(), connectionId = 11 }, Token);
        Assert.Equal(HttpStatusCode.Accepted, prepare.StatusCode);
        Assert.True(prepare.Headers.CacheControl!.NoStore);
        var id = (await prepare.ReadJsonAsync(Token)).GetProperty("id").GetGuid();
        var review = await WaitAsync(admin, "awaitingConfirmation");
        Assert.Equal(id, review.GetProperty("id").GetGuid());
        Assert.Equal("1", review.GetProperty("review").GetProperty("count").GetString());
        Assert.Equal(body.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), review.GetProperty("review").GetProperty("payloadBytes").GetString());
        Assert.False(File.Exists(FilePath(data.Path)));
        using var premature = await admin.GetAsync(new Uri($"{Root}/{id}/download", UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.Conflict, premature.StatusCode);
        using var unconfirmed = await admin.PostJsonAsync($"{Root}/{id}/confirm", new { confirmed = false }, Token);
        Assert.Equal(HttpStatusCode.BadRequest, unconfirmed.StatusCode);
        foreach (var other in new[] { secondSession, another })
        {
            Assert.Equal(JsonValueKind.Null, (await other.GetJsonAsync(Root, Token)).GetProperty("export").ValueKind);
            using var busy = await other.PostJsonAsync(Root, new { state = "failed" }, Token);
            Assert.Equal(HttpStatusCode.Conflict, busy.StatusCode);
            foreach (var suffix in new[] { "confirm", "cancel" })
            {
                using var denied = await other.PostJsonAsync($"{Root}/{id}/{suffix}", new { confirmed = true }, Token);
                Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
            }
            using var readDenied = await other.GetAsync(new Uri($"{Root}/{id}/download", UriKind.Relative), Token);
            Assert.Equal(HttpStatusCode.NotFound, readDenied.StatusCode);
        }
        using (var accepted = await admin.PostJsonAsync($"{Root}/{id}/confirm", new { confirmed = true }, Token))
        { Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode); }
        // Discarded response is recovered through a read, never by re-submitting generation.
        var ready = await WaitAsync(admin, "ready");
        using var repeated = await admin.PostJsonAsync($"{Root}/{id}/confirm", new { confirmed = true }, Token);
        Assert.Equal(HttpStatusCode.Conflict, repeated.StatusCode);
        Assert.True(File.Exists(FilePath(data.Path)));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(FilePath(data.Path)));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(Path.GetDirectoryName(FilePath(data.Path))!));
        }
        Assert.Equal("success", Assert.Single(TestAccounts.Audit(factory.Services, "delivery.export")).Result);
        using var download = await admin.GetAsync(new Uri($"{Root}/{id}/download", UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.True(download.Headers.CacheControl!.NoStore);
        Assert.Equal("attachment", download.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal("application/x-ndjson", download.Content.Headers.ContentType!.MediaType);
        var bytes = await download.Content.ReadAsByteArrayAsync(Token);
        Assert.Equal(bytes.Length, download.Content.Headers.ContentLength);
        Assert.Equal(bytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), ready.GetProperty("fileBytes").GetString());
        using var json = JsonDocument.Parse(bytes);
        Assert.Equal(draft.DeliveryId, json.RootElement.GetProperty("deliveryId").GetGuid());
        Assert.Equal(body, Encoding.UTF8.GetBytes(json.RootElement.GetProperty("content").GetString()!));
        Assert.DoesNotContain("private-credential", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        Assert.False(File.Exists(FilePath(data.Path)));
        await WaitAsync(admin, "responseWritten");
        using var replay = await admin.GetAsync(new Uri($"{Root}/{id}/download", UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
        Assert.Equal("success", Assert.Single(TestAccounts.Audit(factory.Services, "delivery.export.download")).Result);
        Assert.Single(cache.Inspect(new(area)).Items);
        Assert.All(TestAccounts.Audit(factory.Services, "delivery.export"), audit =>
            Assert.DoesNotContain("private-credential", audit.Detail ?? "", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ChangedSelectionAndEmptySelectionCannotPublishPartialFiles()
    {
        using var data = TemporaryDirectory.Create();
        await using var factory = new FixedDataDirectoryApplicationFactory(data.Path);
        TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        var cache = factory.Services.GetRequiredService<DeliveryCache>();
        await SeedAsync(cache, DeliveryState.Failed);
        using var client = await factory.CreateLoggedInClientAsync("admin", Token);
        using var prepared = await client.PostJsonAsync(Root, new { state = "failed" }, Token);
        var review = await WaitAsync(client, "awaitingConfirmation");
        var id = review.GetProperty("id").GetGuid();
        var item = Assert.Single(cache.Inspect(new(DeliveryState.Failed)).Items);
        await cache.CleanupAsync(new(DeliveryState.Failed, [new(item.Sequence, item.DeliveryId, item.ConnectionId, item.MigrationId)]), Token);
        using var confirmed = await client.PostJsonAsync($"{Root}/{id}/confirm", new { confirmed = true }, Token);
        Assert.Equal(HttpStatusCode.Accepted, confirmed.StatusCode);
        Assert.Equal("delivery.exportSelectionChanged", (await WaitAsync(client, "failed")).GetProperty("error").GetString());
        using var denied = await client.GetAsync(new Uri($"{Root}/{id}/download", UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.Conflict, denied.StatusCode);
        Assert.False(File.Exists(FilePath(data.Path)));
        await WaitUntilAsync(() => TestAccounts.Audit(factory.Services, "delivery.export").Count == 1);
        Assert.Equal("failure", Assert.Single(TestAccounts.Audit(factory.Services, "delivery.export")).Result);
        using var empty = await client.PostJsonAsync(Root, new { state = "failed" }, Token);
        Assert.Equal(HttpStatusCode.Accepted, empty.StatusCode);
        var noRecords = await WaitAsync(client, "awaitingConfirmation");
        Assert.Equal("0", noRecords.GetProperty("review").GetProperty("count").GetString());
        using var noConfirm = await client.PostJsonAsync($"{Root}/{noRecords.GetProperty("id").GetGuid()}/confirm", new { confirmed = true }, Token);
        Assert.Equal(HttpStatusCode.Conflict, noConfirm.StatusCode);
    }

    [Fact]
    public async Task InvalidFilterDoesNotOccupySlotAndRevokedSessionCannotDownload()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        await SeedAsync(factory.Services.GetRequiredService<DeliveryCache>(), DeliveryState.Quarantined);
        using var client = await factory.CreateLoggedInClientAsync("admin", Token);
        foreach (var filter in new object[] { new { state = "pending" }, new { state = "failed", connectionId = 0 },
            new { state = "failed", reason = new string('x', 1025) }, new { state = "failed", from = "invalid-time" } })
        {
            using var bad = await client.PostJsonAsync(Root, filter, Token);
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        }
        using var prepared = await client.PostJsonAsync(Root, new { state = "quarantined" }, Token);
        var id = (await WaitAsync(client, "awaitingConfirmation")).GetProperty("id").GetGuid();
        using var confirmed = await client.PostJsonAsync($"{Root}/{id}/confirm", new { confirmed = true }, Token);
        await WaitAsync(client, "ready");
        TestAccounts.Update(factory.Services, "admin", user => user.IsEnabled = false);
        using var denied = await client.GetAsync(new Uri($"{Root}/{id}/download", UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
    }

    internal static string FilePath(string directory) => Path.Combine(directory, DeliveryExportFiles.DirectoryName, DeliveryExportFiles.FileName);
    internal static async Task<DeliveryDraft> SeedAsync(DeliveryCache cache, DeliveryState state, byte[]? body = null)
    {
        var draft = new DeliveryDraft(Guid.NewGuid(), 11, Guid.NewGuid(), "meter", 1, body ?? "body\nwhole"u8.ToArray(),
            "private-credential-fingerprint", "private-credential-summary", "private-credential-address",
            headers: new Dictionary<string, string> { ["fixture"] = "private-credential-header" });
        var seq = Assert.Single((await cache.EnqueueAsync([draft], Token)).Seqs);
        var result = state == DeliveryState.Failed ? await cache.RecordFailureAsync(new(seq, 1, "HTTP 422", true, false), Token)
            : await cache.QuarantineAsync(seq, DeliveryQuarantineReasons.TargetChanged, Token);
        Assert.True(result.Committed);
        return draft;
    }

    internal static async Task<JsonElement> WaitAsync(HttpClient client, string state)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        while (true)
        {
            var current = (await client.GetJsonAsync(Root, timeout.Token)).GetProperty("export");
            if (current.ValueKind != JsonValueKind.Null && current.GetProperty("state").GetString() == state)
            { return current; }
            await Task.Delay(10, timeout.Token);
        }
    }

    internal static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        while (!predicate())
        { await Task.Delay(10, timeout.Token); }
    }
}
