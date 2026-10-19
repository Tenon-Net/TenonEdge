using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TenonEdge.Abstractions.Plans;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.Runtime.Diagnostics;
using TenonEdge.Runtime.Lifecycle;

namespace TenonEdge.IntegrationTests.Runtime;

public sealed class LargeMessageBudgetTests(ITestOutputHelper output)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SustainedOneMiBHttpInputUsesOriginalBoundedCachesAndBudgetThroughRealQueueAndOutput()
    {
        const int mib = 1024 * 1024;
        var selected = new string('x', 128 * 1024);
        var prefix = "{\"v\":9223372036854775807,\"text\":\"" + selected + "\",\"unselected\":\"";
        var payload = Encoding.UTF8.GetBytes(prefix + new string('y', mib - Encoding.UTF8.GetByteCount(prefix) - 2) + "\"}");
        Assert.Equal(mib, payload.Length);
        var devices = new[] { "one", "two" };
        var reads = new ConcurrentDictionary<string, int>(devices.Select(device => new KeyValuePair<string, int>(device, 0)));
        var sent = new ConcurrentDictionary<string, int>(reads);
        var errors = new ConcurrentQueue<string>();
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var peer = builder.Build();
        peer.MapGet("/source/{device}", async (HttpContext context) =>
        {
            var device = context.Request.RouteValues["device"]!.ToString()!;
            context.Response.ContentType = "application/json";
            context.Response.ContentLength = payload.Length;
            await context.Response.Body.WriteAsync(payload, context.RequestAborted);
            reads.AddOrUpdate(device, 1, (_, count) => count + 1);
        });
        peer.MapPost("/output/{device}", async (HttpContext context) =>
        {
            var device = context.Request.RouteValues["device"]!.ToString()!;
            using var document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
            var root = document.RootElement;
            var values = root.GetProperty("values");
            if (root.GetProperty("deviceKey").GetString() != "meter-" + device ||
                root.GetProperty("messageId").GetString() != context.Request.Headers["Idempotency-Key"].ToString() ||
                values.GetProperty("v").GetInt64() != long.MaxValue || values.GetProperty("text").GetString() != selected ||
                values.EnumerateObject().Count() != 2)
            { errors.Enqueue("Output identity, exact value, or selected payload changed."); }
            sent.AddOrUpdate(device, 1, (_, count) => count + 1);
            context.Response.StatusCode = StatusCodes.Status204NoContent;
        });
        await peer.StartAsync(Token);
        var address = new Uri(peer.Urls.Single());
        using var data = TemporaryDirectory.Create();
        using var seeds = TemporaryDirectory.Create();
        var seed = Path.Combine(seeds.Path, "one-mib.json");
        var units = devices.SelectMany(device => new[]
        {
            new RuntimeUnitPlan(RuntimeUnitId.Create("input.http", $"meter-{device}/sample"), JsonSerializer.SerializeToElement(new
            {
                connectionId = 1, baseAddress = address.AbsoluteUri, path = $"source/{device}", periodMs = 1000, timeoutMs = 5000,
                fields = new[] { new { key = "v", path = "/v", type = "integer" }, new { key = "text", path = "/text", type = "string" } },
            })),
            new RuntimeUnitPlan(RuntimeUnitId.Create("output.http", $"route-{device}/out"), JsonSerializer.SerializeToElement(new
            {
                connectionId = 2, baseAddress = address.AbsoluteUri, path = $"output/{device}", timeoutMs = 5000,
                inputs = new[] { $"meter-{device}/sample" },
            })),
        }).ToArray();
        await File.WriteAllTextAsync(seed, JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            units = units.Select(unit => new { kind = unit.Id.Kind, key = unit.Id.Key, configuration = unit.Configuration }),
        }), Token);
        await using var factory = new FixedDataDirectoryApplicationFactory(data.Path, new Dictionary<string, string>
        {
            [RuntimeSettings.SeedSnapshotKey] = seed,
            ["TenonEdge:Outbound:LocalPorts:0"] = address.Port.ToString(CultureInfo.InvariantCulture),
        });
        using var api = factory.CreateClient();
        using var setup = await api.CompleteSetupAsync(Token);
        await api.UseSessionAsync(setup, Token);
        var runtime = factory.Services.GetRequiredService<RuntimeHost>();
        var messages = factory.Services.GetRequiredService<RuntimeMessageCache>();
        var budget = factory.Services.GetRequiredService<InFlightBudget>();
        Assert.Same(messages, runtime.Messages);
        Assert.Same(budget, runtime.InFlight);
        Assert.Equal(64L * mib, budget.Capacity);
        var clock = Stopwatch.StartNew();
        long peakCache = 0;
        var samples = 0;
        var originalRecords = new Dictionary<RuntimeUnitId, long>();

        void CheckBudgets(int globalLimit)
        {
            Assert.Empty(errors);
            var status = messages.Status;
            peakCache = Math.Max(peakCache, status.ChargedBytes);
            Assert.InRange(status.ChargedBytes, 0, globalLimit);
            Assert.InRange(status.StoredBytes, 0, status.ChargedBytes);
            var flight = budget.Status;
            Assert.InRange(flight.ReservedBytes, 0, flight.CapacityBytes);
            Assert.InRange(flight.PeakReservedBytes, 0, flight.CapacityBytes);
            foreach (var unit in units)
            {
                var records = messages.Read(unit.Id, false);
                Assert.InRange(records.Count, 0, RuntimeMessageCache.MaximumObjectRecords);
                Assert.InRange(records.Sum(record => (long)record.StoredBytes + RuntimeMessageCache.MetadataCharge), 0, mib);
                if (records.Count > 0)
                { originalRecords.TryAdd(unit.Id, records[^1].Sequence); }
                Assert.All(records, record =>
                {
                    Assert.True(record.Truncated);
                    Assert.False(record.PrivacyBlocked);
                    Assert.Null(record.Body);
                    Assert.Equal(RuntimeMessageCache.MaximumRecordBytes, record.StoredBytes);
                    Assert.True(record.OriginalBytes > record.StoredBytes);
                    if (unit.Id.Kind == "input.http")
                    { Assert.True(record.OriginalBytes > mib); } // Diagnostic encoding includes the raw input and typed fields.
                });
            }
            samples++;
        }

        async Task WaitForSent(int perDevice, int globalLimit)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            while (devices.Any(device => sent[device] < perDevice))
            {
                CheckBudgets(globalLimit);
                await Task.Delay(25, timeout.Token);
            }
            CheckBudgets(globalLimit);
        }

        // At default limits every object must wrap its 1 MiB bucket, without a page being open.
        await WaitForSent(24, RuntimeMessageCache.MaximumGlobalBytes);
        Assert.Equal(units.Length, originalRecords.Count);
        foreach (var unit in units)
        {
            var records = messages.Read(unit.Id, false);
            Assert.NotEmpty(records);
            Assert.DoesNotContain(records, record => record.Sequence == originalRecords[unit.Id]);
        }
        Assert.True(messages.Status.ChargedBytes > 2 * mib);

        // The existing configurable budget forces cross-object eviction too. No replacement cache or test-only API.
        messages.Configure(mib, 2 * mib, RuntimeMessageCache.MaximumObjectRecords);
        CheckBudgets(2 * mib);
        await WaitForSent(40, 2 * mib);
        foreach (var unit in units)
        {
            var latest = messages.Read(unit.Id, false)[0];
            var page = await api.GetJsonAsync($"/api/edge-admin/v1/runtime/messages?kind={unit.Id.Kind}&key={unit.Id.Key}&revision=1", Token);
            Assert.All(page.GetProperty("items").EnumerateArray(), item =>
            {
                Assert.True(item.GetProperty("truncated").GetBoolean());
                Assert.Equal(RuntimeMessageCache.MaximumRecordBytes, item.GetProperty("storedBytes").GetInt32());
                Assert.False(item.TryGetProperty("body", out _));
            });
            using var response = await api.GetAsync($"/api/edge-admin/v1/runtime/messages/{latest.Sequence}/body?kind={unit.Id.Kind}&key={unit.Id.Key}&revision=1&recordRevision=1&sourceMessageId={latest.SourceMessageId:D}", Token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.ReadJsonAsync(Token);
            Assert.Equal(latest.OriginalBytes, body.GetProperty("record").GetProperty("originalBytes").GetInt32());
            Assert.Equal(RuntimeMessageCache.MaximumRecordBytes, Encoding.UTF8.GetByteCount(body.GetProperty("body").GetString()!));
        }
        await runtime.StopAsync(Token);
        CheckBudgets(2 * mib);
        Assert.Equal(0, budget.Status.ReservedBytes);
        Assert.True(budget.Status.PeakReservedBytes >= 48L * mib);
        Assert.True(runtime.Deliveries.GetStatus().CommittedRecords >= 80);
        Assert.True(runtime.Deliveries.GetStatus().DeletedRecords >= 80);
        output.WriteLine($"FS-08: {payload.Length} bytes/GET; reads={string.Join(',', devices.Select(device => reads[device]))}; posts={string.Join(',', devices.Select(device => sent[device]))}; budgetSamples={samples}; seconds={clock.Elapsed.TotalSeconds:F1}; cachePeakCharged={peakCache}; inFlightPeak={budget.Status.PeakReservedBytes}; finalInFlight={budget.Status.ReservedBytes}. Logical budgets, not a CLR heap/RSS ceiling.");
    }
}
