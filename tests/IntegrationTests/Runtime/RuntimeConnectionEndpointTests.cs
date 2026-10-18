using System.Net;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Abstractions.Contracts;
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

public sealed class RuntimeConnectionEndpointTests
{
    private const string Root = "/api/edge-admin/v1/runtime/connections";
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static DeliveryDraft Delivery(long id) => new(Guid.NewGuid(), id, Guid.NewGuid(), "device", 1,
        "{\"secret\":\"private-response-body\"}"u8.ToArray(), "private-fingerprint", "private-account", "private-address");

    [Theory]
    [InlineData("Administrator")]
    [InlineData("Configurator")]
    [InlineData("Viewer")]
    public async Task AllRolesCanReadNonSecretDraftReferencesAndActualPendingCountsWithoutApplying(string role)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var services = factory.Services;
        TestAccounts.Create(services, "operator", ApiRequests.StrongPassword, Enum.Parse<UserRole>(role));
        using var anonymous = factory.CreateClient();
        using var denied = await anonymous.GetAsync(new Uri(Root, UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        var connections = services.GetRequiredService<ConnectionDraftStore>();
        var connection = connections.Write(null, new("0", "http", "HTTP draft", ConnectionKind.Http, Http: new("http://localhost/")), "fixture", null);
        var device = services.GetRequiredService<DeviceDraftStore>().Write(null,
            new(connection.Version, "meter", "Meter", DeviceKind.Http, ConnectionId: connection.Connection.Id), "fixture", null);
        var input = services.GetRequiredService<InputDraftStore>().Write(device.Device.Id, null,
            new(device.Version, "read", "Read", Enabled: false, Http: new("/values", [])), "fixture", null);
        var graph = new RouteGraph(1, [new("send", "output.http", 1, "Send", JsonSerializer.SerializeToElement(new { connectionId = connection.Connection.Id }))], [], new([]));
        var route = services.GetRequiredService<RouteDraftStore>().Write(null, new(input.Version, "route", "Route", false, graph), "fixture", null);
        var cache = services.GetRequiredService<DeliveryCache>();
        var added = await cache.EnqueueAsync([Delivery(connection.Connection.Id), Delivery(connection.Connection.Id), Delivery(connection.Connection.Id)], Token);
        Assert.True(added.Committed);
        await cache.RecordFailureAsync(new(added.Seqs[0], 0, "rejected", true, false), Token);
        await cache.QuarantineAsync(added.Seqs[1], DeliveryQuarantineReasons.TargetMissing, Token);
        using var client = await factory.CreateLoggedInClientAsync("operator", Token);
        using var response = await client.GetAsync(new Uri(Root + "?expectedDraftVersion=" + route.Version, UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        var data = await response.ReadJsonAsync(Token);
        Assert.True(data.GetProperty("queueAvailable").GetBoolean());
        Assert.Equal(JsonValueKind.Null, data.GetProperty("revision").ValueKind);
        var row = Assert.Single(data.GetProperty("connections").EnumerateArray());
        Assert.Equal("notApplied", row.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("effective").ValueKind);
        Assert.Equal("HTTP draft", row.GetProperty("draft").GetProperty("name").GetString());
        Assert.Equal(1, row.GetProperty("pendingCount").GetInt64());
        Assert.Empty(row.GetProperty("users").EnumerateArray());
        var users = row.GetProperty("draftUsers").EnumerateArray().ToArray();
        Assert.Equal(2, users.Length);
        Assert.Contains(users, user => user.GetProperty("name").GetString() == "Meter / Read" && !user.GetProperty("enabled").GetBoolean());
        Assert.Contains(users, user => user.GetProperty("key").GetString() == "route/send");
        Assert.All(users, user => Assert.False(user.GetProperty("active").GetBoolean()));
        Assert.DoesNotContain("private-", data.GetRawText(), StringComparison.Ordinal);
        Assert.Empty(services.GetRequiredService<RuntimeVersionStore>().ReadHistory());
        Assert.Single(cache.ReadPending(connection.Connection.Id, 0, 10));
        using var changed = await client.GetAsync(new Uri(Root + "?expectedDraftVersion=stale", UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
        using var invalid = await client.GetAsync(new Uri(Root + "?expectedDraftVersion=" + new string('x', 65), UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Fact]
    public async Task EffectiveMetadataAndObservationUseFrozenIdentityNotChangedDraftAndRunningDoesNotMeanConnected()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var services = factory.Services;
        TestAccounts.Create(services, "operator", ApiRequests.StrongPassword, UserRole.Viewer);
        var connection = services.GetRequiredService<ConnectionDraftStore>().Write(null,
            new("0", "http", "Applied name", ConnectionKind.Http, Http: new("http://localhost/old/")), "fixture", null);
        var device = services.GetRequiredService<DeviceDraftStore>().Write(null, new(connection.Version, "meter", "Meter", DeviceKind.Simulator), "fixture", null);
        var input = services.GetRequiredService<InputDraftStore>().Write(device.Device.Id, null,
            new(device.Version, "sample", "Sample", Simulator: new(SampleJson: "{\"value\":42}")), "fixture", null);
        var graph = new RouteGraph(1,
            [new("input", "input.simulator", 1, "Input", JsonSerializer.SerializeToElement(new { sourceIds = new[] { input.Input.Id } })),
             new("send", "output.http", 1, "Send", JsonSerializer.SerializeToElement(new { connectionId = connection.Connection.Id }))],
            [new("input", "send")], new([]));
        var route = services.GetRequiredService<RouteDraftStore>().Write(null, new(input.Version, "route", "Route", true, graph), "fixture", null);
        services.GetRequiredService<OutputControlStore>().Write(connection.Connection.Id, new(true, true), "fixture", null);
        var coordinator = services.GetRequiredService<RuntimeCoordinator>();
        Assert.True((await coordinator.ApplyDraftAsync(route.Version, "fixture", null, Token)).Activated);
        var cache = services.GetRequiredService<DeliveryCache>();
        await OutputPipelineTests.UntilAsync(() => cache.GetQueueTotals(connection.Connection.Id).PendingCount > 0);
        using var client = await factory.CreateLoggedInClientAsync("operator", Token);
        var first = Row(await client.GetJsonAsync(Root, Token));
        Assert.False(first.GetProperty("draftChanged").GetBoolean());
        Assert.Equal("connecting", first.GetProperty("state").GetString()); // Sender loop is running, but paused before any I/O.
        Assert.True(Assert.Single(first.GetProperty("users").EnumerateArray()).GetProperty("active").GetBoolean());
        var changed = services.GetRequiredService<ConnectionDraftStore>().Write(connection.Connection.Id,
            new(route.Version, "http", "Unapplied name", ConnectionKind.Http, Http: new("http://localhost/new/")), "fixture", null);
        // The paused sender already owns the real session; do not substitute a second fixture session.
        var shared = services.GetRequiredService<ISharedConnections>();
        var oldIdentity = JsonSerializer.Serialize(new { BaseAddress = "http://localhost/old/", TimeoutMs = 5000 });
        Assert.NotNull(shared.Observe(connection.Connection.Id, "http", oldIdentity));
        var data = await client.GetJsonAsync(Root, Token);
        Assert.Equal(changed.Version, data.GetProperty("draftVersion").GetString());
        var row = Row(data);
        Assert.Equal("connecting", row.GetProperty("state").GetString());
        Assert.True(row.GetProperty("draftChanged").GetBoolean());
        Assert.Equal("Applied name", row.GetProperty("effective").GetProperty("name").GetString());
        Assert.Equal("http://localhost/old/", row.GetProperty("effective").GetProperty("target").GetString());
        Assert.Equal("Unapplied name", row.GetProperty("draft").GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("lastErrorCode").ValueKind);
        var draftIdentity = JsonSerializer.Serialize(new { BaseAddress = "http://localhost/new/", TimeoutMs = 5000 });
        Assert.Null(shared.Observe(connection.Connection.Id, "http", draftIdentity));
        var database = services.GetRequiredService<ConfigDatabase>();
        database.Client.Updateable<RuntimeSnapshotRecord>().SetColumns(value => value.ContentHash == "corrupted")
            .Where(value => value.Revision == 1).ExecuteCommand();
        using var unavailable = await client.GetAsync(new Uri(Root, UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.Conflict, unavailable.StatusCode);
        Assert.Equal("runtime.snapshotUnavailable", await unavailable.ReadProblemCodeAsync(Token));
    }

    [Fact]
    public async Task QueueOnlyUnitsAreNotUsersAndDisabledOrUnusedConnectionsDoNotPretendToBeConnected()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var services = factory.Services;
        TestAccounts.Create(services, "operator", ApiRequests.StrongPassword, UserRole.Viewer);
        var store = services.GetRequiredService<ConnectionDraftStore>();
        var disabled = store.Write(null, new("0", "disabled", "Disabled", ConnectionKind.Http, Enabled: false, Http: new("http://localhost/")), "fixture", null);
        var unused = store.Write(null, new(disabled.Version, "unused", "Unused", ConnectionKind.Mqtt, Mqtt: new("localhost")), "fixture", null);
        Assert.True((await services.GetRequiredService<RuntimeCoordinator>().ApplyDraftAsync(unused.Version, "fixture", null, Token)).Activated);
        using var client = await factory.CreateLoggedInClientAsync("operator", Token);
        var rows = (await client.GetJsonAsync(Root, Token)).GetProperty("connections").EnumerateArray().ToArray();
        Assert.Equal("disabled", rows.Single(row => row.GetProperty("id").GetInt64() == disabled.Connection.Id).GetProperty("state").GetString());
        var idle = rows.Single(row => row.GetProperty("id").GetInt64() == unused.Connection.Id);
        Assert.Equal("unused", idle.GetProperty("state").GetString());
        Assert.Empty(idle.GetProperty("users").EnumerateArray());
        Assert.Equal(0, idle.GetProperty("pendingCount").GetInt64());
    }

    [Fact]
    public async Task QueueFailureRetainsMetadataButNeverReturnsZeroCounts()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var services = factory.Services;
        TestAccounts.Create(services, "operator", ApiRequests.StrongPassword, UserRole.Viewer);
        var connection = services.GetRequiredService<ConnectionDraftStore>().Write(null,
            new("0", "http", "HTTP", ConnectionKind.Http, Http: new("http://localhost/")), "fixture", null);
        Assert.True((await services.GetRequiredService<RuntimeCoordinator>().ApplyDraftAsync(connection.Version, "fixture", null, Token)).Activated);
        var cache = services.GetRequiredService<DeliveryCache>();
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = cache.Options.DatabasePath, Pooling = false }.ToString());
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "ALTER TABLE delivery RENAME TO fixture_unavailable";
        command.ExecuteNonQuery();
        try
        {
            using var client = await factory.CreateLoggedInClientAsync("operator", Token);
            var data = await client.GetJsonAsync(Root, Token);
            Assert.False(data.GetProperty("queueAvailable").GetBoolean());
            var row = Row(data);
            Assert.Equal("HTTP", row.GetProperty("effective").GetProperty("name").GetString());
            Assert.Equal(JsonValueKind.Null, row.GetProperty("pendingCount").ValueKind);
            Assert.Equal("unknown", row.GetProperty("state").GetString());
        }
        finally
        {
            command.CommandText = "ALTER TABLE fixture_unavailable RENAME TO delivery";
            command.ExecuteNonQuery();
        }
    }

    [Fact]
    public async Task PendingCountQueryCoversBothCatalogsWithZerosAndExcludesFailedAndQuarantined()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var cache = factory.Services.GetRequiredService<DeliveryCache>();
        var write = await cache.EnqueueAsync([Delivery(1), Delivery(1), Delivery(1), Delivery(1000)], Token);
        Assert.True(write.Committed);
        await cache.RecordFailureAsync(new(write.Seqs[0], 0, "rejected", true, false), Token);
        await cache.QuarantineAsync(write.Seqs[1], DeliveryQuarantineReasons.TargetMissing, Token);
        var counts = cache.GetPendingCounts(Enumerable.Range(1, 1000).Select(id => (long)id).ToArray());
        Assert.Equal(1000, counts.Count);
        Assert.Equal(1, counts[1]);
        Assert.Equal(0, counts[2]);
        Assert.Equal(1, counts[1000]);
        Assert.Single(cache.GetPendingCounts([1, 1]));
        Assert.Empty(cache.GetPendingCounts([]));
        Assert.Throws<ArgumentException>(() => cache.GetPendingCounts([0]));
        Assert.Throws<ArgumentException>(() => cache.GetPendingCounts(Enumerable.Range(1, 1001).Select(id => (long)id).ToArray()));
    }

    private static JsonElement Row(JsonElement list) => Assert.Single(list.GetProperty("connections").EnumerateArray());
}
