using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Host.Features.Configuration;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Devices;
using TenonEdge.Host.Features.Inputs;
using TenonEdge.Host.Features.Routes;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Security;
using TenonEdge.Host.Storage;
using TenonEdge.Host.Storage.Migrations;
using TenonEdge.IntegrationTests.Infrastructure;

namespace TenonEdge.IntegrationTests.Runtime;

public sealed class ConfigurationEditTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static RuntimePlanDocument Empty => RuntimePlanDocument.Parse("""{"schemaVersion":1,"units":[]}"""u8);

    [Fact]
    public async Task EveryObjectKindReportsItsActualLastEditorAndRetainsFrozenMetadata()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var start = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var connection = factory.Services.GetRequiredService<ConnectionDraftStore>().Write(null,
            new("0", "http", "HTTP", ConnectionKind.Http, Http: new("http://localhost/")), "connection-editor", null);
        var devices = factory.Services.GetRequiredService<DeviceDraftStore>();
        var device = devices.Write(null, new(connection.Version, "meter", "Meter", DeviceKind.Simulator), "device-editor", null);
        var input = factory.Services.GetRequiredService<InputDraftStore>().Write(device.Device.Id, null,
            new(device.Version, "sample", "Sample", Simulator: new()), "input-editor", null);
        var route = factory.Services.GetRequiredService<RouteDraftStore>().Write(null,
            new(input.Version, "route", "Route", false, new(1, [], [], new([]))), "route-editor", null);
        var versions = factory.Services.GetRequiredService<RuntimeVersionStore>();
        var review = versions.ReviewDraft(route.Version, Token);
        Assert.Equal(4, review.Changes.Length);
        Assert.All(review.Changes, change =>
        {
            Assert.Equal(change.Kind + "-editor", change.UpdatedBy);
            Assert.InRange(change.UpdatedAt!.Value, start, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        });
        var revision = versions.FreezeDraft(Empty, route.Version, "applier");
        versions.Commit(revision, null);
        var edited = devices.Write(device.Device.Id, new(route.Version, "meter", "Changed", DeviceKind.Simulator), "second-editor", null);
        Assert.Equal("second-editor", Assert.Single(versions.ReviewDraft(edited.Version, Token).Changes).UpdatedBy);
        Assert.Equal("device-editor", RuntimeSnapshotDocument.Read(versions.Load(revision)!).Draft!.Edits!.Single(edit => edit.Kind == "device").UpdatedBy);
    }

    [Theory]
    [InlineData("connection")]
    [InlineData("device")]
    [InlineData("input")]
    [InlineData("route")]
    public async Task EachDeleteEntryPointReportsTheDeletionActor(string kind)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var connections = factory.Services.GetRequiredService<ConnectionDraftStore>();
        var devices = factory.Services.GetRequiredService<DeviceDraftStore>();
        var inputs = factory.Services.GetRequiredService<InputDraftStore>();
        var routes = factory.Services.GetRequiredService<RouteDraftStore>();
        var connection = connections.Write(null, new("0", "http", "HTTP", ConnectionKind.Http, Http: new("http://localhost/")), "creator", null);
        var device = devices.Write(null, new(connection.Version, "meter", "Meter", DeviceKind.Simulator), "creator", null);
        var input = inputs.Write(device.Device.Id, null, new(device.Version, "sample", "Sample", Simulator: new()), "creator", null);
        var route = routes.Write(null, new(input.Version, "route", "Route", false, new(1, [], [], new([]))), "creator", null);
        var versions = factory.Services.GetRequiredService<RuntimeVersionStore>();
        versions.Commit(versions.FreezeDraft(Empty, route.Version, "applier"), null);
        var deleted = kind switch
        {
            "connection" => connections.Delete(connection.Connection.Id, route.Version, "deleter", null),
            "device" => devices.Delete(device.Device.Id, route.Version, "deleter", null),
            "input" => inputs.Delete(device.Device.Id, input.Input.Id, route.Version, "deleter", null),
            _ => routes.Delete(route.Route.Id, route.Version, "deleter", null),
        };
        var changes = versions.ReviewDraft(deleted.Version, Token).Changes;
        Assert.Contains(changes, change => change.Kind == kind);
        Assert.All(changes, change => { Assert.Equal("removed", change.Change); Assert.Equal("deleter", change.UpdatedBy); Assert.NotNull(change.UpdatedAt); });
    }

    [Fact]
    public async Task MetadataOnlyChangesDoNotMakeConfigurationDirtyAndAuditFailureRollsBackMetadata()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var devices = factory.Services.GetRequiredService<DeviceDraftStore>();
        var saved = devices.Write(null, new("0", "meter", "Meter", DeviceKind.Simulator), "creator", null);
        var versions = factory.Services.GetRequiredService<RuntimeVersionStore>();
        versions.Commit(versions.FreezeDraft(Empty, saved.Version, "applier"), null);
        var same = devices.Write(saved.Device.Id, new(saved.Version, "meter", "Meter", DeviceKind.Simulator), "resaver", null);
        Assert.Empty(versions.ReviewDraft(same.Version, Token).Changes);
        var database = factory.Services.GetRequiredService<ConfigDatabase>();
        var before = database.InTransaction(ConfigurationEditJournal.Read);
        database.Client.Ado.ExecuteCommand("CREATE TRIGGER deny_edit_audit BEFORE INSERT ON audit_log WHEN NEW.action = 'device.draftUpdated' BEGIN SELECT RAISE(ABORT, 'fixture'); END");
        try
        {
            Assert.ThrowsAny<Exception>(() => devices.Write(saved.Device.Id, new(same.Version, "meter", "Rejected", DeviceKind.Simulator), "rejected-editor", null));
            Assert.Equal(before, database.InTransaction(ConfigurationEditJournal.Read));
            Assert.Equal(same.Version, devices.Read().Version);
            Assert.Equal("Meter", Assert.Single(devices.Read().Devices).Name);
        }
        finally { database.Client.Ado.ExecuteCommand("DROP TRIGGER deny_edit_audit"); }
    }

    [Fact]
    public async Task NeverFrozenCreateDeleteDoesNotAccumulateTombstones()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var devices = factory.Services.GetRequiredService<DeviceDraftStore>();
        var version = "0";
        for (var i = 0; i < 10; i++)
        {
            var saved = devices.Write(null, new(version, "meter", "Meter", DeviceKind.Simulator), "creator", null);
            version = devices.Delete(saved.Device.Id, saved.Version, "deleter", null).Version;
        }
        Assert.Empty(factory.Services.GetRequiredService<ConfigDatabase>().InTransaction(ConfigurationEditJournal.Read));
        Assert.Empty(factory.Services.GetRequiredService<RuntimeVersionStore>().ReviewDraft(version, Token).Changes);
    }

    [Fact]
    public async Task DeleteAfterFreezeIsRetainedUntilTheDeletionItselfCommitsAndHistoryKeepsItsActor()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var devices = factory.Services.GetRequiredService<DeviceDraftStore>();
        var saved = devices.Write(null, new("0", "meter", "Meter", DeviceKind.Simulator), "creator", null);
        var versions = factory.Services.GetRequiredService<RuntimeVersionStore>();
        var first = versions.FreezeDraft(Empty, saved.Version, "applier");
        var deleted = devices.Delete(saved.Device.Id, saved.Version, "deleter", null);
        versions.Commit(first, null);
        var change = Assert.Single(versions.ReviewDraft(deleted.Version, Token).Changes);
        Assert.Equal("removed", change.Change);
        Assert.Equal("deleter", change.UpdatedBy);
        var second = versions.FreezeDraft(Empty, deleted.Version, "applier");
        versions.Commit(second, null);
        Assert.Empty(factory.Services.GetRequiredService<ConfigDatabase>().InTransaction(ConfigurationEditJournal.Read));
        Assert.Equal("deleter", Assert.Single(versions.CompareVersions(first, second)).UpdatedBy);
        Assert.Empty(versions.ReviewDraft(deleted.Version, Token).Changes);
    }

    [Fact]
    public async Task DeviceCopyAndCascadeDeleteTrackTheCopyingAndDeletingActorsForAllSources()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var devices = factory.Services.GetRequiredService<DeviceDraftStore>();
        var source = devices.Write(null, new("0", "meter", "Meter", DeviceKind.Simulator), "creator", null);
        var input = factory.Services.GetRequiredService<InputDraftStore>().Write(source.Device.Id, null,
            new(source.Version, "sample", "Sample", Simulator: new()), "creator", null);
        var copied = devices.Copy(source.Device.Id, new(input.Version, 1), "copier", null);
        var versions = factory.Services.GetRequiredService<RuntimeVersionStore>();
        var review = versions.ReviewDraft(copied.Version, Token);
        Assert.Equal(2, review.Changes.Count(change => change.UpdatedBy == "copier"));
        versions.Commit(versions.FreezeDraft(Empty, copied.Version, "applier"), null);
        var deleted = devices.Delete(Assert.Single(copied.Devices).Id, copied.Version, "deleter", null);
        var changes = versions.ReviewDraft(deleted.Version, Token).Changes;
        Assert.Equal(2, changes.Length);
        Assert.All(changes, change => { Assert.Equal("removed", change.Change); Assert.Equal("deleter", change.UpdatedBy); });
    }

    [Fact]
    public async Task RollbackReplacementRestoresCheckpointMetadataButConcurrentEditsKeepTheirAuthor()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var devices = factory.Services.GetRequiredService<DeviceDraftStore>();
        var saved = devices.Write(null, new("0", "meter", "First", DeviceKind.Simulator), "first-editor", null);
        var versions = factory.Services.GetRequiredService<RuntimeVersionStore>();
        versions.Commit(versions.FreezeDraft(Empty, saved.Version, "applier"), null);
        var edited = devices.Write(saved.Device.Id, new(saved.Version, "meter", "Second", DeviceKind.Simulator), "second-editor", null);
        versions.Commit(versions.FreezeDraft(Empty, edited.Version, "applier"), null);
        var rollback = versions.FreezeManualRollback(1, edited.Version, "rollback-operator");
        Assert.True(versions.Commit(rollback.Revision, null, replaceDraftVersion: edited.Version));
        var database = factory.Services.GetRequiredService<ConfigDatabase>();
        Assert.Equal("first-editor", Assert.Single(database.InTransaction(ConfigurationEditJournal.Read)).UpdatedBy);
        var current = devices.Read().Version;
        var next = versions.FreezeManualRollback(2, current, "rollback-operator");
        var concurrent = devices.Write(saved.Device.Id, new(current, "meter", "Concurrent", DeviceKind.Simulator), "concurrent-editor", null);
        Assert.False(versions.Commit(next.Revision, null, replaceDraftVersion: current));
        Assert.Equal("concurrent-editor", Assert.Single(versions.ReviewDraft(concurrent.Version, Token).Changes).UpdatedBy);
    }

    [Fact]
    public async Task RestartedReviewApiReportsTheSavedEditorNotTheCurrentViewer()
    {
        using var directory = TemporaryDirectory.Create();
        string version;
        long? editedAt;
        await using (var factory = new FixedDataDirectoryApplicationFactory(directory.Path))
        {
            TestAccounts.Create(factory.Services, "operator", ApiRequests.StrongPassword, UserRole.Configurator);
            var device = factory.Services.GetRequiredService<DeviceDraftStore>().Write(null,
                new("0", "meter", "Meter", DeviceKind.Simulator), "original-editor", null);
            version = device.Version;
            editedAt = Assert.Single(factory.Services.GetRequiredService<RuntimeVersionStore>().ReviewDraft(version, Token).Changes).UpdatedAt;
        }
        await using var restarted = new FixedDataDirectoryApplicationFactory(directory.Path);
        using var client = await restarted.CreateLoggedInClientAsync("operator", Token);
        using var response = await client.PostJsonAsync("/api/edge-admin/v1/configuration/review", new ConfigurationReviewRequest(version), Token);
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        var review = await response.ReadJsonAsync(Token);
        var change = Assert.Single(review.GetProperty("changes").EnumerateArray());
        Assert.Equal("original-editor", change.GetProperty("updatedBy").GetString());
        Assert.Equal(editedAt, change.GetProperty("updatedAt").GetInt64());
    }

    [Fact]
    public void MigrationPreservesKnownRouteMetadataAndDoesNotInventLegacyDeviceEditors()
    {
        using var store = TestConfigStore.Create();
        store.Initialize(ConfigMigrations.All.Where(migration => migration.Version <= 16).ToArray());
        store.Database.Client.Ado.ExecuteCommand("INSERT INTO device_draft(id,business_key,content) VALUES (123,'meter','{}')");
        store.Database.Client.Ado.ExecuteCommand("INSERT INTO route_draft(id,business_key,name,enabled,node_count,updated_by,updated_at,graph) VALUES (456,'route','Route',0,0,'known-editor',123456,'{}')");
        store.CreateMigrator().Migrate();
        var metadata = store.Database.InTransaction(ConfigurationEditJournal.Read);
        Assert.Equal(2, metadata.Length);
        Assert.Null(metadata.Single(edit => edit.Kind == "device").UpdatedBy);
        Assert.Null(metadata.Single(edit => edit.Kind == "device").UpdatedAt);
        Assert.Equal("known-editor", metadata.Single(edit => edit.Kind == "route").UpdatedBy);
        Assert.Equal(123456, metadata.Single(edit => edit.Kind == "route").UpdatedAt);
        store.CreateMigrator().Migrate();
        Assert.Equal(metadata, store.Database.InTransaction(ConfigurationEditJournal.Read));
    }
}
