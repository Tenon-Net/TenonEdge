using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TenonEdge.Host.Features.Deliveries;
using TenonEdge.Host.Features.Routes;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Security;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.IntegrationTests.Runtime;
using TenonEdge.Runtime.Deliveries;

namespace TenonEdge.IntegrationTests.Deliveries;

public sealed class FailedDeliveryRetryApiTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private const string Endpoint = "/api/edge-admin/v1/deliveries/retry";
    private static readonly string[] Inputs = ["meter/sample"];

    [Fact]
    public async Task RetryApiPreservesFrozenBodyAndPauseAndUsesTheOriginalSenderWithRoleCsrfAndGenerationChecks()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        await using var peer = builder.Build();
        peer.Urls.Add("http://127.0.0.1:0");
        var calls = new ConcurrentQueue<string>();
        var code = 422;
        peer.MapPost("/send", async (HttpContext context) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            calls.Enqueue(await reader.ReadToEndAsync(Token));
            context.Response.StatusCode = Volatile.Read(ref code);
        });
        await peer.StartAsync(Token);
        try
        {
            using var data = TemporaryDirectory.Create();
            using var seeds = TemporaryDirectory.Create();
            var address = new Uri(peer.Urls.Single());
            var seed = Path.Combine(seeds.Path, "seed.json");
            await File.WriteAllTextAsync(seed, JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                units = new object[] {
                    new { kind = "input.simulator", key = "meter/sample", configuration = new { periodMs = 1000, sample = new { value = 42 } } },
                    new { kind = "output.http", key = "http", configuration = new { connectionId = 11, baseAddress = address.AbsoluteUri, path = "send", inputs = Inputs } },
                },
            }), Token);
            await using var factory = new FixedDataDirectoryApplicationFactory(data.Path, new Dictionary<string, string>
            { [RuntimeSettings.SeedSnapshotKey] = seed, ["TenonEdge:Outbound:LocalPorts:0"] = address.Port.ToString(CultureInfo.InvariantCulture) });
            foreach (var role in new[] { UserRole.Administrator, UserRole.Configurator, UserRole.Viewer })
            { TestAccounts.Create(factory.Services, role.ToString(), ApiRequests.StrongPassword, role); }
            var cache = factory.Services.GetRequiredService<DeliveryCache>();
            var coordinator = factory.Services.GetRequiredService<RuntimeCoordinator>();
            await OutputPipelineTests.UntilAsync(() => cache.ReadFailed(11, 0, 10).Count != 0);
            var record = cache.ReadFailed(11, 0, 10)[0];
            var controls = factory.Services.GetRequiredService<OutputControlStore>();
            var paused = controls.Write(11, new(true, true), "Administrator", null);
            await OutputPipelineTests.UntilAsync(() => coordinator.Runtime.GetOutputSenders()[0].Sender.State == "manuallyPaused");
            var request = new DeliveryRetryRequest(1, [new(record.Seq, record.DeliveryId)]);
            using var anonymous = factory.CreateClient();
            using var anonymousResponse = await anonymous.PostJsonAsync(Endpoint, request, Token);
            Assert.Equal(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);
            using var viewer = await factory.CreateLoggedInClientAsync("Viewer", Token);
            using var forbidden = await viewer.PostJsonAsync(Endpoint, request, Token);
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
            using var editor = await factory.CreateLoggedInClientAsync("Configurator", Token);
            using var invalid = await editor.PostJsonAsync(Endpoint, request with { Items = [] }, Token);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            using var duplicate = await editor.PostJsonAsync(Endpoint, request with { Items = [request.Items![0], request.Items[0]] }, Token);
            Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);
            using var stale = await editor.PostJsonAsync(Endpoint, request with { Revision = 999 }, Token);
            Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
            var csrf = editor.DefaultRequestHeaders.GetValues(ApiRequests.CsrfHeader).Single();
            editor.DefaultRequestHeaders.Remove(ApiRequests.CsrfHeader);
            using var csrfDenied = await editor.PostJsonAsync(Endpoint, request, Token);
            Assert.Equal(HttpStatusCode.Forbidden, csrfDenied.StatusCode);
            editor.DefaultRequestHeaders.Add(ApiRequests.CsrfHeader, csrf);
            var sends = calls.Count;
            using var retried = await editor.PostJsonAsync(Endpoint, request, Token);
            Assert.Equal(HttpStatusCode.OK, retried.StatusCode);
            Assert.True(retried.Headers.CacheControl!.NoStore);
            using var result = JsonDocument.Parse(await retried.Content.ReadAsStringAsync(Token));
            Assert.Equal(1, result.RootElement.GetProperty("requeuedRecords").GetInt32());
            var moved = Assert.Single(cache.ReadPending(11, 0, 100), item => item.DeliveryId == record.DeliveryId);
            Assert.True(moved.Seq > record.Seq);
            Assert.Equal(record.CreatedAt, moved.CreatedAt);
            Assert.Equal(record.Payload.ToArray(), moved.Payload.ToArray());
            Assert.True(controls.Read(11).ManuallyPaused);
            Assert.Equal(sends, calls.Count);
            using var replay = await editor.PostJsonAsync(Endpoint, request, Token);
            Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
            Assert.Contains(TestAccounts.Audit(factory.Services, "delivery.retryFailed"), item => item.Result == "success");
            Volatile.Write(ref code, 200);
            controls.Write(11, new(false, true, paused.ResumeVersion), "Administrator", null);
            var body = System.Text.Encoding.UTF8.GetString(record.Payload.Span);
            await OutputPipelineTests.UntilAsync(() => calls.Count(text => text == body) == 2);
            await OutputPipelineTests.UntilAsync(() => cache.ReadPending(11, 0, 100).All(item => item.DeliveryId != record.DeliveryId));
            Assert.Equal(1, cache.GetPersistentCounters()["manuallyRetriedRecords"]);
        }
        finally { await peer.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task OwnedRetrySurvivesCanceledWaitBlocksApplyAndShutdownWaitsForItsWriter()
    {
        using var data = TemporaryDirectory.Create();
        // Seed creates an effective target; this scenario holds SQLite's real writer lock, not an API fault switch.
        using var seeds = TemporaryDirectory.Create();
        var seed = Path.Combine(seeds.Path, "seed.json");
        await File.WriteAllTextAsync(seed, """
            {"schemaVersion":1,"units":[{"kind":"input.simulator","key":"meter/sample","configuration":{"periodMs":60000,"sample":{"v":42}}},{"kind":"output.http","key":"out","configuration":{"connectionId":11,"baseAddress":"http://127.0.0.1:1/","path":"send","inputs":["meter/sample"]}}]}
            """, Token);
        await using var factory = new FixedDataDirectoryApplicationFactory(data.Path, new Dictionary<string, string> { [RuntimeSettings.SeedSnapshotKey] = seed });
        var coordinator = factory.Services.GetRequiredService<RuntimeCoordinator>();
        var controls = factory.Services.GetRequiredService<OutputControlStore>();
        controls.Write(11, new(true, true), "admin", null);
        var cache = factory.Services.GetRequiredService<DeliveryCache>();
        Assert.True(coordinator.Status.State == RuntimeCoordinatorState.Active, coordinator.Status.Reason);
        var fingerprint = coordinator.Runtime.CaptureOutputFingerprints(1)![11];
        var draft = new DeliveryDraft(Guid.NewGuid(), 11, Guid.NewGuid(), "meter", 1, "{}"u8.ToArray(), fingerprint, "test-owner", "send");
        var seq = Assert.Single((await cache.EnqueueAsync([draft], Token)).Seqs);
        await cache.RecordFailureAsync(new(seq, 0, "rejected", true, false), Token);
        using var connection = new SqliteConnection($"Data Source={Path.Combine(data.Path, "queue.db")};Pooling=False");
        connection.Open();
        using var transaction = connection.BeginTransaction();
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var pending = coordinator.RetryFailedDeliveriesAsync(1, [new(seq, draft.DeliveryId)], "admin", null, wait.Token);
        Assert.True(coordinator.Status.QueueOperationInProgress);
        await Assert.ThrowsAsync<DeliveryCleanupUnavailableException>(() => coordinator.CleanupDeliveriesAsync(
            new(DeliveryState.Failed, [new(seq, draft.DeliveryId, 11, null)]), "admin", null, Token));
        var version = factory.Services.GetRequiredService<RouteDraftStore>().Read().Version;
        await Assert.ThrowsAsync<RuntimeApplicationUnavailableException>(() => coordinator.ApplyDraftAsync(version, "admin", null, Token));
        await Assert.ThrowsAsync<DeliveryRetryUnavailableException>(() => coordinator.RetryFailedDeliveriesAsync(1, [new(seq, draft.DeliveryId)], "admin", null, Token));
        await wait.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        var stop = coordinator.StopAsync(Token);
        Assert.False(stop.IsCompleted);
        transaction.Commit();
        await stop.WaitAsync(TimeSpan.FromSeconds(15), Token);
        Assert.False(coordinator.Status.QueueOperationInProgress);
        Assert.Equal(RuntimeCoordinatorState.Stopped, coordinator.Status.State);
        var moved = Assert.Single(cache.ReadPending(11, 0, 10), item => item.DeliveryId == draft.DeliveryId);
        Assert.Equal(draft.DeliveryId, moved.DeliveryId);
        Assert.True(moved.Seq > seq);
        Assert.Single(TestAccounts.Audit(factory.Services, "delivery.retryFailed"));
    }
}
