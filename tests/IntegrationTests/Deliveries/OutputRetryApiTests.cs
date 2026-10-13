using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TenonEdge.Host.Features.Deliveries;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Security;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.IntegrationTests.Runtime;
using TenonEdge.Runtime.Outputs;

namespace TenonEdge.IntegrationTests.Deliveries;

public sealed class OutputRetryApiTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private const string Endpoint = "/api/edge-admin/v1/connections/11/retry-now";
    private static readonly string[] Inputs = ["meter/sample"];

    [Fact]
    public async Task AuthorizedSignalUsesTheOriginalHttpSenderAndCannotCrossBackoffPauseOrRevisionBoundaries()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        await using var peer = builder.Build();
        peer.Urls.Add("http://127.0.0.1:0");
        var calls = new ConcurrentQueue<string>();
        var responseCode = 503;
        peer.MapPost("/send", async (HttpContext context) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            calls.Enqueue(await reader.ReadToEndAsync(Token));
            context.Response.StatusCode = Volatile.Read(ref responseCode);
            context.Response.Headers.RetryAfter = "60";
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
            {
                [RuntimeSettings.SeedSnapshotKey] = seed,
                ["TenonEdge:Outbound:LocalPorts:0"] = address.Port.ToString(CultureInfo.InvariantCulture),
            });
            foreach (var role in new[] { UserRole.Administrator, UserRole.Configurator, UserRole.Viewer })
            { TestAccounts.Create(factory.Services, role.ToString(), ApiRequests.StrongPassword, role); }
            var runtime = factory.Services.GetRequiredService<RuntimeCoordinator>().Runtime;
            OutputSenderDiagnostics Sender() => Assert.Single(runtime.GetOutputSenders()).Sender;
            await OutputPipelineTests.UntilAsync(() => runtime.GetOutputSenders().FirstOrDefault()?.Sender.BackoffVersion is not null);
            var original = new OutputRetryRequest(1, Sender().BackoffVersion);
            using var anonymous = factory.CreateClient();
            using var anon = await anonymous.PostJsonAsync(Endpoint, original, Token);
            Assert.Equal(HttpStatusCode.Unauthorized, anon.StatusCode);
            using var viewer = await factory.CreateLoggedInClientAsync("Viewer", Token);
            using var forbidden = await viewer.PostJsonAsync(Endpoint, original, Token);
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
            using var editor = await factory.CreateLoggedInClientAsync("Configurator", Token);
            using var invalid = await editor.PostJsonAsync(Endpoint, original with { BackoffVersion = null }, Token);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            using var stale = await editor.PostJsonAsync(Endpoint, original with { Revision = 999 }, Token);
            Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
            using var unknown = await editor.PostJsonAsync(Endpoint, original with { BackoffVersion = Guid.NewGuid().ToString("N") }, Token);
            Assert.Equal(HttpStatusCode.Conflict, unknown.StatusCode);
            var csrf = editor.DefaultRequestHeaders.GetValues(ApiRequests.CsrfHeader).Single();
            editor.DefaultRequestHeaders.Remove(ApiRequests.CsrfHeader);
            using var csrfDenied = await editor.PostJsonAsync(Endpoint, original, Token);
            Assert.Equal(HttpStatusCode.Forbidden, csrfDenied.StatusCode);
            editor.DefaultRequestHeaders.Add(ApiRequests.CsrfHeader, csrf);
            Assert.Single(calls);
            using var accepted = await editor.PostJsonAsync(Endpoint, original, Token);
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
            Assert.True(accepted.Headers.CacheControl!.NoStore);
            await OutputPipelineTests.UntilAsync(() => calls.Count == 2 && Sender().BackoffVersion is { } next && next != original.BackoffVersion);
            Assert.Equal(calls.ToArray()[0], calls.ToArray()[1]); // Same frozen body/deliveryId, not a newly generated send.
            using var replay = await editor.PostJsonAsync(Endpoint, original, Token);
            Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
            Assert.Single(TestAccounts.Audit(factory.Services, "delivery.retryNow"));
            var controls = factory.Services.GetRequiredService<OutputControlStore>();
            var current = new OutputRetryRequest(1, Sender().BackoffVersion);
            var paused = controls.Write(11, new(true, true), "Administrator", null);
            using var deniedPause = await editor.PostJsonAsync(Endpoint, current, Token);
            Assert.Equal(HttpStatusCode.Conflict, deniedPause.StatusCode);
            await OutputPipelineTests.UntilAsync(() => Sender().State == "manuallyPaused");
            Assert.Equal(2, calls.Count);
            Volatile.Write(ref responseCode, 401);
            using var admin = await factory.CreateLoggedInClientAsync("Administrator", Token);
            using var resumed = await admin.PutAsJsonAsync(new Uri("/api/edge-admin/v1/connections/11/delivery-control", UriKind.Relative),
                new OutputControlRequest(false, true, paused.ResumeVersion), Token);
            Assert.Equal(HttpStatusCode.OK, resumed.StatusCode);
            await OutputPipelineTests.UntilAsync(() => Sender().State == "automaticallyPaused");
            Assert.Null(Sender().BackoffVersion);
            using var deniedAuto = await editor.PostJsonAsync(Endpoint, current, Token);
            Assert.Equal(HttpStatusCode.Conflict, deniedAuto.StatusCode);
            Assert.Equal(3, calls.Count);
            Assert.Single(TestAccounts.Audit(factory.Services, "delivery.pause"));
            Assert.Single(TestAccounts.Audit(factory.Services, "delivery.resume"));
            await runtime.StopAsync(Token);
            Assert.False(runtime.RequestOutputRetry(11, 1, current.BackoffVersion!));
        }
        finally { await peer.StopAsync(CancellationToken.None); }
    }
}
