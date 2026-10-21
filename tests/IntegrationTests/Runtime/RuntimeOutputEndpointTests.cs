using System.Net;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Deliveries;
using TenonEdge.Host.Features.Devices;
using TenonEdge.Host.Features.Inputs;
using TenonEdge.Host.Features.Routes;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Security;
using TenonEdge.Host.Storage;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.Runtime.Deliveries;

namespace TenonEdge.IntegrationTests.Runtime;

public sealed class RuntimeOutputEndpointTests
{
    private const string Root = "/api/edge-admin/v1/runtime/outputs";
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static DeliveryDraft Draft(long connection) => new(Guid.NewGuid(), connection, Guid.NewGuid(), "device", 1,
        "{\"secret\":\"output-private-body\"}"u8.ToArray(), "private-fingerprint", "private-recipient", "private-address");

    [Theory]
    [InlineData(null)]
    [InlineData("tmpfs")]
    [InlineData("ext4")]
    public async Task ActiveCapacityReportsActualCalibrationApplicabilityWithoutChangingStorageProtection(string? filesystem)
    {
        using var data = TemporaryDirectory.Create();
        await using var factory = new FixedDataDirectoryApplicationFactory(data.Path, configureServices: services =>
        {
            services.RemoveAll<DeliveryCache>();
            services.AddSingleton(provider => new DeliveryCache(new()
            {
                DatabasePath = Path.Combine(provider.GetRequiredService<DataDirectoryLock>().Directory.Root, DeliveryCacheOptions.FileName),
                ConfigStoreId = provider.GetRequiredService<GatewayIdentity>().ConfigStoreId,
            }, provider.GetRequiredService<TimeProvider>(), () => new(100_000_000_000, 90_000_000_000, filesystem)));
        });
        var services = factory.Services;
        TestAccounts.Create(services, "viewer", ApiRequests.StrongPassword, UserRole.Viewer);
        var device = services.GetRequiredService<DeviceDraftStore>().Write(null, new("0", "meter", "Meter", DeviceKind.Simulator), "test", null);
        var input = services.GetRequiredService<InputDraftStore>().Write(device.Device.Id, null,
            new(device.Version, "sample", "Sample", Simulator: new(SampleJson: "{\"v\":42}")), "test", null);
        var connection = services.GetRequiredService<ConnectionDraftStore>().Write(null,
            new(input.Version, "http", "HTTP", ConnectionKind.Http, Http: new("http://localhost/")), "test", null);
        var graph = new RouteGraph(1,
            [new("in", "input.simulator", 1, "Input", JsonSerializer.SerializeToElement(new { sourceIds = new[] { input.Input.Id } })),
             new("out", "output.http", 1, "Output", JsonSerializer.SerializeToElement(new { connectionId = connection.Connection.Id }))],
            [new("in", "out")], new([]));
        var route = services.GetRequiredService<RouteDraftStore>().Write(null, new(connection.Version, "route", "Route", true, graph), "test", null);
        services.GetRequiredService<OutputControlStore>().Write(connection.Connection.Id, new(true, true), "fixture", null);
        Assert.True((await services.GetRequiredService<RuntimeCoordinator>().ApplyDraftAsync(route.Version, "test", null, Token)).Activated);
        var cache = services.GetRequiredService<DeliveryCache>();
        await OutputPipelineTests.UntilAsync(() => cache.OutputTraffic.Read(connection.Connection.Id).RecordsPerSecond > 0);
        using var client = await factory.CreateLoggedInClientAsync("viewer", Token);
        var page = await client.GetJsonAsync(Root, Token);
        var item = Assert.Single(page.GetProperty("items").EnumerateArray());
        var capacity = item.GetProperty("capacity");
        Assert.True(capacity.GetProperty("quotaUpperBoundSeconds").GetDouble() > 0);
        Assert.False(page.GetProperty("storageProtection").GetProperty("rejectNewRecords").GetBoolean());
        Assert.Equal(filesystem, page.GetProperty("storageProtection").GetProperty("space").GetProperty("fileSystem").GetString());
        var status = capacity.GetProperty("calibration").GetProperty("status").GetString();
        if (filesystem == "ext4" && OperatingSystem.IsLinux())
        {
            Assert.Equal("applicable", status);
            Assert.True(capacity.GetProperty("seconds").GetDouble() > 0);
            Assert.Equal("linux-ext4-sqlite-3.53.3-queue9-v1", capacity.GetProperty("calibration").GetProperty("profile").GetString());
        }
        else
        {
            Assert.Equal(filesystem is null ? "environmentUnknown" : "unsupportedEnvironment", status);
            Assert.Equal(JsonValueKind.Null, capacity.GetProperty("seconds").ValueKind);
            Assert.Equal(JsonValueKind.Null, capacity.GetProperty("calibration").GetProperty("amplification").ValueKind);
        }
    }

    [Theory]
    [InlineData("Administrator")]
    [InlineData("Configurator")]
    [InlineData("Viewer")]
    public async Task MetadataOnlyPagesIncludeOrphansManualStateAndRealQueueCountsForAllRoles(string role)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "operator", ApiRequests.StrongPassword, Enum.Parse<UserRole>(role));
        using var anon = factory.CreateClient();
        foreach (var path in new[] { Root, Root + "/1" })
        {
            using var denied = await anon.GetAsync(new Uri(path, UriKind.Relative), Token);
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        }
        var cache = factory.Services.GetRequiredService<DeliveryCache>();
        var write = await cache.EnqueueAsync([Draft(1), Draft(1), Draft(2), Draft(3)], Token);
        Assert.True(write.Committed);
        await cache.RecordFailureAsync(new(write.Seqs[0], 0, "test-rejected", true, false), Token);
        await cache.QuarantineAsync(write.Seqs[2], DeliveryQuarantineReasons.TargetMissing, Token);
        factory.Services.GetRequiredService<OutputControlStore>().Write(1, new(true, true), "fixture", null);
        using var client = await factory.CreateLoggedInClientAsync("operator", Token);
        using var response = await client.GetAsync(new Uri(Root + "?limit=2", UriKind.Relative), Token);
        Assert.True(response.Headers.CacheControl!.NoStore);
        var page = await response.ReadJsonAsync(Token);
        Assert.Equal(2, page.GetProperty("usage").GetProperty("pendingCount").GetInt32());
        Assert.Equal(1, page.GetProperty("usage").GetProperty("failedCount").GetInt32());
        Assert.Equal(1, page.GetProperty("usage").GetProperty("targetMissingCount").GetInt32());
        Assert.Equal(2, page.GetProperty("nextConnectionId").GetInt32());
        var first = page.GetProperty("items")[0];
        Assert.False(first.GetProperty("active").GetBoolean());
        Assert.Equal("manuallyPaused", first.GetProperty("state").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, first.GetProperty("name").ValueKind);
        Assert.Equal(0, first.GetProperty("traffic").GetProperty("points").GetArrayLength());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, first.GetProperty("capacity").GetProperty("seconds").ValueKind);
        Assert.DoesNotContain("output-private", page.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("private-", page.GetRawText(), StringComparison.Ordinal);
        var next = await client.GetJsonAsync(Root + "?after=2&revision=0", Token);
        Assert.Equal(3, next.GetProperty("items")[0].GetProperty("connectionId").GetInt32());
        var detail = await client.GetJsonAsync(Root + "/1?revision=0", Token);
        Assert.Equal(1, detail.GetProperty("items").GetArrayLength());
        Assert.Equal(1, detail.GetProperty("items")[0].GetProperty("queue").GetProperty("counters").GetProperty("rejected").GetInt32());
        Assert.Equal(1, detail.GetProperty("items")[0].GetProperty("queue").GetProperty("usage").GetProperty("pendingCount").GetInt32());
        using var missing = await client.GetAsync(new Uri(Root + "/999", UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using var changed = await client.GetAsync(new Uri(Root + "?revision=1", UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
        foreach (var path in new[] { "?limit=0", "?limit=101", "?after=-1", "?revision=-1", "/0", "/9007199254740992" })
        {
            using var invalid = await client.GetAsync(new Uri(Root + path, UriKind.Relative), Token);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }
        Assert.Single(TestAccounts.Audit(factory.Services, "delivery.pause"));
        Assert.Empty(TestAccounts.Audit(factory.Services, "delivery.resume"));
    }
    [Fact]
    public async Task ActiveMetadataUsesFrozenConnectionAndCorruptSnapshotCannotUseDraftAsReplacement()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "operator", ApiRequests.StrongPassword, UserRole.Viewer);
        var services = factory.Services;
        var device = services.GetRequiredService<DeviceDraftStore>().Write(null, new("0", "meter", "Meter", DeviceKind.Simulator), "test", null);
        var input = services.GetRequiredService<InputDraftStore>().Write(device.Device.Id, null,
            new(device.Version, "sample", "Sample", Simulator: new(SampleJson: "{\"v\":42}")), "test", null);
        var connections = services.GetRequiredService<ConnectionDraftStore>();
        var connection = connections.Write(null, new(input.Version, "http", "Frozen HTTP name", ConnectionKind.Http,
            Http: new("http://localhost/")), "test", null);
        var graph = new RouteGraph(1,
            [new("in", "input.simulator", 1, "Input", JsonSerializer.SerializeToElement(new { sourceIds = new[] { input.Input.Id } })),
             new("out", "output.http", 1, "Output", JsonSerializer.SerializeToElement(new { connectionId = connection.Connection.Id }))],
            [new("in", "out")], new([]));
        var route = services.GetRequiredService<RouteDraftStore>().Write(null, new(connection.Version, "route", "Route", true, graph), "test", null);
        services.GetRequiredService<OutputControlStore>().Write(connection.Connection.Id, new(true, true), "fixture", null);
        var coordinator = services.GetRequiredService<RuntimeCoordinator>();
        Assert.True((await coordinator.ApplyDraftAsync(route.Version, "test", null, Token)).Activated);
        var changed = connections.Write(connection.Connection.Id, new(route.Version, "http", "Unapplied name", ConnectionKind.Http,
            Http: new("http://localhost/")), "test", null);
        await OutputPipelineTests.UntilAsync(() => services.GetRequiredService<DeliveryCache>().GetQueueTotals(connection.Connection.Id).PendingCount > 0);
        using var client = await factory.CreateLoggedInClientAsync("operator", Token);
        var page = await client.GetJsonAsync(Root, Token);
        var item = Assert.Single(page.GetProperty("items").EnumerateArray());
        Assert.True(item.GetProperty("active").GetBoolean());
        Assert.Equal("Frozen HTTP name", item.GetProperty("name").GetString());
        Assert.Equal("http", item.GetProperty("key").GetString());
        Assert.Equal("manuallyPaused", item.GetProperty("state").GetString());
        Assert.Equal(1, page.GetProperty("activeStates").GetProperty("manuallyPaused").GetInt32());
        Assert.DoesNotContain("Unapplied", page.GetRawText(), StringComparison.Ordinal);
        var nextGraph = graph with { Nodes = [graph.Nodes[0], graph.Nodes[1] with { Id = "next" }], Edges = [new("in", "next")] };
        var renamed = services.GetRequiredService<RouteDraftStore>().Write(route.Route.Id,
            new(changed.Version, "route", "Renamed", true, nextGraph), "test", null);
        Assert.True((await coordinator.ApplyDraftAsync(renamed.Version, "test", null, Token)).Activated);
        var retained = await client.GetJsonAsync(Root, Token);
        Assert.Equal(2, retained.GetProperty("revision").GetInt32());
        Assert.Equal("running", retained.GetProperty("items")[0].GetProperty("unitState").GetString());
        services.GetRequiredService<ConfigDatabase>().Client.Updateable<RuntimeSnapshotRecord>()
            .SetColumns(row => row.ContentHash == "broken").Where(row => row.Revision == 2).ExecuteCommand();
        using var damaged = await client.GetAsync(new Uri(Root, UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.Conflict, damaged.StatusCode);
        Assert.Equal("runtime.snapshotUnavailable", await damaged.ReadProblemCodeAsync(Token));
    }

    [Fact]
    public async Task UnreadableQueueDoesNotBecomeZeroOccupancy()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "operator", ApiRequests.StrongPassword, UserRole.Viewer);
        var cache = factory.Services.GetRequiredService<DeliveryCache>();
        _ = cache.GetQueueReport([]);
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = cache.Options.DatabasePath, Pooling = false }.ToString());
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "ALTER TABLE delivery RENAME TO fixture_unavailable";
        command.ExecuteNonQuery();
        using var client = await factory.CreateLoggedInClientAsync("operator", Token);
        using var response = await client.GetAsync(new Uri(Root, UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("runtime.queueUnavailable", await response.ReadProblemCodeAsync(Token));
        command.CommandText = "ALTER TABLE fixture_unavailable RENAME TO delivery";
        command.ExecuteNonQuery();
    }

}
