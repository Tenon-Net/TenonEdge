using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Devices;
using TenonEdge.Host.Features.Inputs;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Security;
using TenonEdge.Host.Storage;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.Runtime.Deliveries;

namespace TenonEdge.IntegrationTests.Connections;

public sealed class ConnectionDraftTests
{
    private const string Endpoint = "/api/edge-admin/v1/connections/";
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("Viewer", HttpStatusCode.Forbidden)]
    [InlineData("Configurator", HttpStatusCode.OK)]
    [InlineData("Administrator", HttpStatusCode.OK)]
    public async Task RolesReadDraftsButOnlyEditorsCanMutate(string role, HttpStatusCode expected)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "operator", ApiRequests.StrongPassword, Enum.Parse<UserRole>(role));
        using var client = await factory.CreateLoggedInClientAsync("operator", Token);
        using var read = await client.GetAsync(new Uri(Endpoint, UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        using var write = await client.PostJsonAsync(Endpoint, Request(), Token);
        Assert.Equal(expected, write.StatusCode);
        Assert.Equal(expected == HttpStatusCode.OK ? 1 : 0, TestAccounts.Audit(factory.Services, "connection.draftCreated").Count);
        var store = factory.Services.GetRequiredService<ConnectionDraftStore>();
        if (expected == HttpStatusCode.Forbidden)
        {
            var created = store.Write(null, Request(), "test", null);
            using var update = await client.PutAsJsonAsync(new Uri(Endpoint + created.Connection.Id, UriKind.Relative), Request(created.Version), Token);
            Assert.Equal(HttpStatusCode.Forbidden, update.StatusCode);
            using var delete = await client.DeleteAsync(new Uri($"{Endpoint}{created.Connection.Id}?expectedVersion={created.Version}", UriKind.Relative), Token);
            Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);
        }
    }

    [Fact]
    public async Task AnonymousAndCsrfFailuresHaveNoWrites()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        using var anonymous = factory.CreateClient();
        using var read = await anonymous.GetAsync(new Uri(Endpoint, UriKind.Relative), Token);
        using var denied = await anonymous.PostJsonAsync(Endpoint, Request(), Token);
        Assert.Equal(HttpStatusCode.Unauthorized, read.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        using var admin = await factory.CreateLoggedInClientAsync("admin", Token);
        admin.DefaultRequestHeaders.Remove(ApiRequests.CsrfHeader);
        using var csrf = await admin.PostJsonAsync(Endpoint, Request(), Token);
        Assert.Equal(HttpStatusCode.Forbidden, csrf.StatusCode);
        Assert.Empty(factory.Services.GetRequiredService<ConnectionDraftStore>().Read().Connections);
    }

    [Fact]
    public async Task CrudPersistsAcrossRestartAndDoesNotChangeRuntimePointers()
    {
        using var directory = TemporaryDirectory.Create();
        long id;
        string version;
        await using (var factory = new FixedDataDirectoryApplicationFactory(directory.Path))
        {
            TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
            using var client = await factory.CreateLoggedInClientAsync("admin", Token);
            using var write = await client.PostJsonAsync(Endpoint, Request(), Token);
            Assert.Equal(HttpStatusCode.OK, write.StatusCode);
            var result = await write.ReadJsonAsync(Token);
            id = result.GetProperty("connection").GetProperty("id").GetInt64();
            version = result.GetProperty("version").GetString()!;
            Assert.InRange(id, 1, 9007199254740991L);
            var state = factory.Services.GetRequiredService<ConfigDatabase>().Client.Queryable<SystemStateRecord>().InSingle(1);
            Assert.Null(state.PendingRevision);
            Assert.Null(state.EffectiveRevision);
            Assert.Null(state.PreviousRevision);
        }
        await using var restarted = new FixedDataDirectoryApplicationFactory(directory.Path);
        using var admin = await restarted.CreateLoggedInClientAsync("admin", Token);
        var store = restarted.Services.GetRequiredService<ConnectionDraftStore>();
        Assert.Equal(version, store.Read().Version);
        Assert.Equal(id, Assert.Single(store.Read().Connections).Id);
        using var changed = await admin.PutAsJsonAsync(new Uri(Endpoint + id, UriKind.Relative), Request(version) with { Name = "updated", Enabled = false }, Token);
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        var updated = store.Read();
        Assert.False(updated.Connections[0].Enabled);
        using var deleted = await admin.DeleteAsync(new Uri($"{Endpoint}{id}?expectedVersion={updated.Version}", UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        Assert.Empty(store.Read().Connections);
        Assert.Single(TestAccounts.Audit(restarted.Services, "connection.draftUpdated"));
        Assert.Single(TestAccounts.Audit(restarted.Services, "connection.draftDeleted"));
    }

    [Fact]
    public async Task DeletionReviewReportsAllRetainedReferencesAndExactQueueStateWithoutDeleting()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        using var client = await factory.CreateLoggedInClientAsync("admin", Token);
        using var created = await client.PostJsonAsync(Endpoint, Request(), Token);
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var response = await created.ReadJsonAsync(Token);
        var id = response.GetProperty("connection").GetProperty("id").GetInt64();
        var version = response.GetProperty("version").GetString()!;

        _ = factory.Services.GetRequiredService<ConnectionDraftStore>().ReviewDeletion(id, version);

        using var review = await client.GetAsync(new Uri($"{Endpoint}{id}/deletion-review?expectedVersion={version}", UriKind.Relative), Token);
        Assert.True(review.StatusCode == HttpStatusCode.OK, $"{review.StatusCode}: {await review.Content.ReadAsStringAsync(Token)}");
        Assert.True(review.Headers.CacheControl?.NoStore);
        var report = await review.ReadJsonAsync(Token);
        Assert.Equal(version, report.GetProperty("version").GetString());
        Assert.True(report.GetProperty("canDelete").GetBoolean());
        Assert.Empty(report.GetProperty("devices").EnumerateArray());
        Assert.Empty(report.GetProperty("routes").EnumerateArray());
        Assert.Empty(report.GetProperty("versions").EnumerateArray());
        Assert.Equal(0, report.GetProperty("pendingCount").GetInt64());
        Assert.Equal(0, report.GetProperty("failedCount").GetInt64());
        Assert.Equal(0, report.GetProperty("quarantinedCount").GetInt64());
        Assert.NotNull(factory.Services.GetRequiredService<ConnectionDraftStore>().Read().Connections.SingleOrDefault(item => item.Id == id));

        using var stale = await client.GetAsync(new Uri($"{Endpoint}{id}/deletion-review?expectedVersion=old", UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.NotNull(factory.Services.GetRequiredService<ConnectionDraftStore>().Read().Connections.SingleOrDefault(item => item.Id == id));
    }

    [Fact]
    public async Task DeletionReviewListsDeviceSourceRetainedUnitAndAllAccumulationPartitions()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        using var client = await factory.CreateLoggedInClientAsync("admin", Token);
        var connection = factory.Services.GetRequiredService<ConnectionDraftStore>().Write(null, Request(), "admin", null);
        var device = factory.Services.GetRequiredService<DeviceDraftStore>().Write(null,
            new(connection.Version, "meter", "Meter", DeviceKind.Modbus, ConnectionId: connection.Connection.Id, UnitId: 1), "admin", null);
        var input = factory.Services.GetRequiredService<InputDraftStore>().Write(device.Device.Id, null,
            new(device.Version, "fast", "Fast", Modbus: new(5000, [new() { Key = "temperature", FunctionCode = 3, Address = 0, Type = "uint16" }])), "admin", null);
        var cache = factory.Services.GetRequiredService<DeliveryCache>();
        var insert = await cache.EnqueueAsync(Enumerable.Range(0, 3).Select(_ => new DeliveryDraft(Guid.NewGuid(), connection.Connection.Id, Guid.NewGuid(), "meter", 1,
            Encoding.UTF8.GetBytes("{}"), "fixture-fingerprint", "fixture", "fixture", 1)).ToArray(), Token);
        Assert.True(insert.Committed);
        Assert.True((await cache.RecordFailureAsync(new(insert.Seqs[1], 0, "fixture", true, false), Token)).Committed);
        Assert.True((await cache.QuarantineAsync(insert.Seqs[2], "fixture", Token)).Committed);

        // A retained plan fixture checks cross-draft references without starting external I/O.
        var plan = RuntimePlanDocument.Parse(Encoding.UTF8.GetBytes("{\"schemaVersion\":1,\"units\":[{\"kind\":\"output.http\",\"key\":\"output\",\"configuration\":{\"connectionId\":" + connection.Connection.Id + ",\"baseAddress\":\"http://10.0.0.1/\",\"inputs\":[\"meter/fast\"]}}]}"));
        var database = factory.Services.GetRequiredService<ConfigDatabase>();
        database.InTransaction(db =>
        {
            db.Insertable(new RuntimeSnapshotRecord
            {
                Revision = 1,
                SchemaVersion = 1,
                Content = plan.CanonicalText,
                ContentHash = plan.ContentHash,
                Origin = RuntimeSnapshotOrigins.Seed,
                State = RuntimeSnapshotStates.Effective,
                CreatedBy = "fixture"
            }).ExecuteCommand();
            var state = db.Queryable<SystemStateRecord>().InSingle(1);
            state.EffectiveRevision = 1;
            db.Updateable(state).ExecuteCommand();
            return true;
        });
        using var response = await client.GetAsync(new Uri($"{Endpoint}{connection.Connection.Id}/deletion-review?expectedVersion={input.Version}", UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var report = await response.ReadJsonAsync(Token);
        Assert.False(report.GetProperty("canDelete").GetBoolean());
        Assert.Contains(report.GetProperty("devices").EnumerateArray(), item => item.GetProperty("name").GetString() == "Meter");
        Assert.Contains(report.GetProperty("versions").EnumerateArray(), item => item.GetProperty("units").EnumerateArray().Any(unit => unit.GetString() == "output.http/output"));
        Assert.Equal(1, report.GetProperty("pendingCount").GetInt64());
        Assert.Equal(1, report.GetProperty("failedCount").GetInt64());
        Assert.Equal(1, report.GetProperty("quarantinedCount").GetInt64());
    }

    [Fact]
    public async Task ConcurrentSavesAndStaleDeletesCannotLoseOtherEdits()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        using var client = await factory.CreateLoggedInClientAsync("admin", Token);
        var requests = await Task.WhenAll(client.PostJsonAsync(Endpoint, Request(), Token), client.PostJsonAsync(Endpoint, Request() with { Key = "other" }, Token));
        using var first = requests[0];
        using var second = requests[1];
        Assert.Single(requests, response => response.StatusCode == HttpStatusCode.OK);
        Assert.Single(requests, response => response.StatusCode == HttpStatusCode.Conflict);
        var store = factory.Services.GetRequiredService<ConnectionDraftStore>();
        var snapshot = store.Read();
        var entry = Assert.Single(snapshot.Connections);
        using var stale = await client.DeleteAsync(new Uri($"{Endpoint}{entry.Id}?expectedVersion=0", UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(snapshot.Version, store.Read().Version);
        using var duplicate = await client.PostJsonAsync(Endpoint, Request(snapshot.Version) with { Key = entry.Key }, Token);
        Assert.Equal("connection.duplicateKey", await duplicate.ReadProblemCodeAsync(Token));
        using var changedKey = await client.PutAsJsonAsync(new Uri(Endpoint + entry.Id, UriKind.Relative), Request(snapshot.Version) with { Key = "replacement" }, Token);
        Assert.Equal("connection.immutableIdentity", await changedKey.ReadProblemCodeAsync(Token));
        Assert.Equal(snapshot.Version, store.Read().Version);
    }

    [Fact]
    public async Task AuditFailureRollsBackObjectAndGlobalDraftVersion()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        using var client = await factory.CreateLoggedInClientAsync("admin", Token);
        var db = factory.Services.GetRequiredService<ConfigDatabase>();
        db.Client.Ado.ExecuteCommand("CREATE TRIGGER fail_draft_audit BEFORE INSERT ON audit_log WHEN NEW.action = 'connection.draftCreated' BEGIN SELECT RAISE(ABORT, 'audit failure'); END;");
        using var failed = await client.PostJsonAsync(Endpoint, Request(), Token);
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        var snapshot = factory.Services.GetRequiredService<ConnectionDraftStore>().Read();
        Assert.Equal("0", snapshot.Version);
        Assert.Empty(snapshot.Connections);
    }

    [Theory]
    [InlineData("\"password\":\"fixture-secret\",")]
    [InlineData("\"credential\":{\"token\":\"fixture-secret\"},")]
    public async Task UnknownSecretPropertiesAreRejectedBeforeStorage(string extra)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        using var client = await factory.CreateLoggedInClientAsync("admin", Token);
        using var content = new StringContent("{" + extra + "\"expectedVersion\":\"0\",\"key\":\"plc\",\"name\":\"PLC\",\"kind\":\"modbus\",\"modbus\":{\"host\":\"10.0.0.1\"}}", Encoding.UTF8, "application/json");
        using var rejected = await client.PostAsync(new Uri(Endpoint, UriKind.Relative), content, Token);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Empty(factory.Services.GetRequiredService<ConnectionDraftStore>().Read().Connections);
        Assert.Empty(TestAccounts.Audit(factory.Services, "connection.draftCreated"));
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("failed")]
    [InlineData("quarantined")]
    public async Task DeliveryRecordsPreventDeletionWithoutChangingDraft(string area)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        using var client = await factory.CreateLoggedInClientAsync("admin", Token);
        var store = factory.Services.GetRequiredService<ConnectionDraftStore>();
        var created = store.Write(null, Request(), "admin", null);
        var cache = factory.Services.GetRequiredService<DeliveryCache>();
        Assert.True((await cache.EnqueueAsync([new DeliveryDraft(Guid.NewGuid(), created.Connection.Id, Guid.NewGuid(), "meter", 1,
            Encoding.UTF8.GetBytes("{}"), "fixture-fingerprint", "fixture", "fixture", 1)], Token)).Committed);
        var seq = Assert.Single(cache.ReadPending(created.Connection.Id, 0, 1)).Seq;
        if (area == "failed")
        { Assert.True((await cache.RecordFailureAsync(new(seq, 0, "fixture", true, false), Token)).Committed); }
        if (area == "quarantined")
        { Assert.True((await cache.QuarantineAsync(seq, "fixture", Token)).Committed); }
        using var rejected = await client.DeleteAsync(new Uri($"{Endpoint}{created.Connection.Id}?expectedVersion={created.Version}", UriKind.Relative), Token);
        Assert.Equal("connection.hasDeliveries", await rejected.ReadProblemCodeAsync(Token));
        Assert.Equal(created.Version, store.Read().Version);
        Assert.Single(store.Read().Connections);
        Assert.Empty(TestAccounts.Audit(factory.Services, "connection.draftDeleted"));
    }

    [Theory]
    [InlineData("effective")]
    [InlineData("previous")]
    [InlineData("pending")]
    public async Task RetainedRuntimeReferencesPreventDeletion(string referenceKind)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        using var client = await factory.CreateLoggedInClientAsync("admin", Token);
        var store = factory.Services.GetRequiredService<ConnectionDraftStore>();
        var created = store.Write(null, Request(), "admin", null);
        // Stored snapshot fixture only: no external HTTP request is started.
        var text = "{\"schemaVersion\":1,\"units\":[{\"kind\":\"output.http\",\"key\":\"output\",\"configuration\":{\"connectionId\":" + created.Connection.Id + ",\"baseAddress\":\"http://10.0.0.1/\",\"inputs\":[\"meter/input\"]}}]}";
        var plan = RuntimePlanDocument.Parse(Encoding.UTF8.GetBytes(text));
        var database = factory.Services.GetRequiredService<ConfigDatabase>();
        database.InTransaction(db =>
        {
            db.Insertable(new RuntimeSnapshotRecord
            {
                Revision = 1,
                SchemaVersion = 1,
                Content = plan.CanonicalText,
                ContentHash = plan.ContentHash,
                Origin = RuntimeSnapshotOrigins.Seed,
                State = RuntimeSnapshotStates.Effective,
                CreatedBy = "fixture"
            }).ExecuteCommand();
            var state = db.Queryable<SystemStateRecord>().InSingle(1);
            if (referenceKind == "effective")
            { state.EffectiveRevision = 1; }
            if (referenceKind == "previous")
            { state.PreviousRevision = 1; }
            if (referenceKind == "pending")
            { state.PendingRevision = 1; }
            db.Updateable(state).ExecuteCommand();
            return true;
        });
        using var rejected = await client.DeleteAsync(new Uri($"{Endpoint}{created.Connection.Id}?expectedVersion={created.Version}", UriKind.Relative), Token);
        Assert.Equal("connection.referenced", await rejected.ReadProblemCodeAsync(Token));
        Assert.Equal(created.Version, store.Read().Version);
        Assert.Single(store.Read().Connections);
    }

    private static ConnectionDraftRequest Request(string version = "0") => new(version, "plc-1", "PLC", ConnectionKind.Modbus, Modbus: new("10.0.0.1"));
}
