using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
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

public sealed class ClearPendingApiTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly string[] Inputs = ["meter/sample"];
    private const string Endpoint = "/api/edge-admin/v1/connections/11/clear-pending";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OriginalHttpSendCompletesBeforeClearWithRoleCsrfConfirmationAuditAndOwnedLifetime(bool cancelledCaller)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        await using var peer = builder.Build();
        peer.Urls.Add("http://127.0.0.1:0");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new ConcurrentQueue<string>();
        peer.MapPost("/send", async (HttpContext context) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            calls.Enqueue(await reader.ReadToEndAsync(Token));
            await release.Task.WaitAsync(Token);
            context.Response.StatusCode = 204;
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
                    new { kind = "output.http", key = "http", configuration = new { connectionId = 11, baseAddress = address.AbsoluteUri, path = "send", timeoutMs = 60000, inputs = Inputs } },
                },
            }), Token);
            await using var factory = new FixedDataDirectoryApplicationFactory(data.Path, new Dictionary<string, string>
            {
                [RuntimeSettings.SeedSnapshotKey] = seed,
                ["TenonEdge:Outbound:LocalPorts:0"] = address.Port.ToString(CultureInfo.InvariantCulture),
            });
            try
            {
                foreach (var role in new[] { UserRole.Administrator, UserRole.Configurator, UserRole.Viewer })
                { TestAccounts.Create(factory.Services, role.ToString(), ApiRequests.StrongPassword, role); }
                var coordinator = factory.Services.GetRequiredService<RuntimeCoordinator>();
                var cache = coordinator.Runtime.Deliveries;
                await OutputPipelineTests.UntilAsync(() => calls.Count == 1 && cache.GetQueueTotals(11).PendingCount >= 2);
                // Manual pause retains the already in-flight HTTP request, but prevents any next one.
                factory.Services.GetRequiredService<OutputControlStore>().Write(11, new(true, true), "Administrator", null);
                using var admin = await factory.CreateLoggedInClientAsync("Administrator", Token);
                var request = new DeliveryClearPendingRequest(1, true);
                if (!cancelledCaller)
                {
                    using var anonymous = factory.CreateClient();
                    using var denied = await anonymous.PostJsonAsync(Endpoint, request, Token);
                    Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
                    foreach (var role in new[] { "Configurator", "Viewer" })
                    {
                        using var client = await factory.CreateLoggedInClientAsync(role, Token);
                        using var forbidden = await client.PostJsonAsync(Endpoint, request, Token);
                        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
                    }
                    var csrf = admin.DefaultRequestHeaders.GetValues(ApiRequests.CsrfHeader).Single();
                    admin.DefaultRequestHeaders.Remove(ApiRequests.CsrfHeader);
                    using var noCsrf = await admin.PostJsonAsync(Endpoint, request, Token);
                    Assert.Equal(HttpStatusCode.Forbidden, noCsrf.StatusCode);
                    admin.DefaultRequestHeaders.Add(ApiRequests.CsrfHeader, csrf);
                    foreach (var invalid in new[] { request with { Confirmed = false }, request with { Revision = 0 } })
                    {
                        using var response = await admin.PostJsonAsync(Endpoint, invalid, Token);
                        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
                    }
                    using var stale = await admin.PostJsonAsync(Endpoint, request with { Revision = 999 }, Token);
                    Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
                    using var missing = await admin.PostJsonAsync(Endpoint.Replace("/11/", "/12/", StringComparison.Ordinal), request, Token);
                    Assert.Equal(HttpStatusCode.Conflict, missing.StatusCode);
                }
                // Role/login checks are not part of the protocol-timeout scenario. Under a full parallel suite
                // they can exceed the default 5 s; keep the deliberately held HTTP request alive for this fixture.
                Assert.Null(Assert.Single(coordinator.Runtime.GetOutputSenders()).Sender.LastResult);
                using var caller = CancellationTokenSource.CreateLinkedTokenSource(Token);
                var clearing = admin.PostJsonAsync(Endpoint, request, caller.Token);
                await OutputPipelineTests.UntilAsync(() => coordinator.Status.QueueOperationInProgress);
                Assert.False(clearing.IsCompleted);
                using var conflict = await admin.PostJsonAsync(Endpoint, request, Token);
                Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
                var version = factory.Services.GetRequiredService<RouteDraftStore>().Read().Version;
                await Assert.ThrowsAsync<RuntimeApplicationUnavailableException>(() => coordinator.ApplyDraftAsync(version, "Administrator", null, Token));
                Task? stop = null;
                if (cancelledCaller)
                {
                    caller.Cancel();
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => clearing);
                    stop = coordinator.StopAsync(Token);
                    Assert.False(stop.IsCompleted);
                }
                release.TrySetResult();
                if (stop is not null)
                { await stop.WaitAsync(TimeSpan.FromSeconds(15), Token); }
                else
                {
                    using var response = await clearing;
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                    Assert.True(response.Headers.CacheControl!.NoStore);
                    using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token));
                    Assert.Equal(cache.GetPersistentCounters()["manuallyClearedPendingRecords"], json.RootElement.GetProperty("clearedRecords").GetInt64());
                    await coordinator.StopAsync(Token);
                }
                Assert.Single(calls);
                Assert.Equal(1, cache.GetStatus().DeletedRecords);
                Assert.True(cache.GetPersistentCounters()["manuallyClearedPendingRecords"] >= 1);
                Assert.True(factory.Services.GetRequiredService<OutputControlStore>().Read(11).ManuallyPaused);
                var audit = TestAccounts.Audit(factory.Services, "delivery.clearPending").Where(entry => entry.Result == "success").ToArray();
                Assert.Equal("Administrator", Assert.Single(audit).AccountName);
                Assert.Contains(cache.GetPersistentCounters()["manuallyClearedPendingRecords"].ToString(CultureInfo.InvariantCulture) + "条", audit[0].Detail, StringComparison.Ordinal);
                Assert.DoesNotContain(calls.Single(), audit[0].Detail ?? "", StringComparison.Ordinal);
                Assert.False(coordinator.Status.QueueOperationInProgress);
            }
            finally { release.TrySetResult(); }
        }
        finally { release.TrySetResult(); await peer.StopAsync(CancellationToken.None); }
    }
}
