using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Host.Features.Configuration;
using TenonEdge.Host.Features.Configuration.Transfer;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Credentials;
using TenonEdge.Host.Features.Devices;
using TenonEdge.Host.Features.Inputs;
using TenonEdge.Host.Features.Routes;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Security;
using TenonEdge.Host.Storage;
using TenonEdge.IntegrationTests.Infrastructure;

namespace TenonEdge.IntegrationTests.Configuration;

public sealed class ConfigurationImportStoreTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly PortableConfiguration Empty = new(1, [], [], [], []);
    private static ConfigDatabase Database(IServiceProvider services) => services.GetRequiredService<ConfigDatabase>();
    private static ConfigurationImportStore Store(IServiceProvider services)
    {
        if (TestAccounts.Find(services, "importer") is null)
        { TestAccounts.Create(services, "importer", ApiRequests.StrongPassword, UserRole.Configurator); }
        return ActivatorUtilities.CreateInstance<ConfigurationImportStore>(services);
    }
    private static ManagementActor Actor(IServiceProvider services)
    {
        var user = TestAccounts.Find(services, "importer")!;
        return new(user.Id, user.SecurityStamp, "127.0.0.1");
    }
    private static ConfigurationCheckpoint Draft(IServiceProvider services) => Database(services).InTransaction(client =>
        ConfigurationCheckpoint.Read(client, ConfigurationDraft.ReadVersion(client)));
    private static byte[] Bytes(PortableConfiguration file) => ConfigurationExport.Serialize(file, Token);
    private static PortableConfiguration Export(IServiceProvider services) => ConfigurationImportDocument.Read(
        services.GetRequiredService<ConfigurationExportStore>().Export(null, Token), Token);
    private static ConnectionDraftResult Connection(IServiceProvider services, string key = "receiver") => services.GetRequiredService<ConnectionDraftStore>()
        .Write(null, new(Draft(services).Version, key, key, ConnectionKind.Http, Http: new("https://example.com/base/")), "original", null);
    private static async Task<HttpCredentialMetadata> Credential(IServiceProvider services, ConnectionDraftResult connection, string? expected = null, string secret = "fixture-secret") =>
        await services.GetRequiredService<HttpCredentialStore>().WriteAsync(connection.Connection.Id,
            new("https://example.com/base/", HttpCredentialKind.Bearer, Secret: secret, ExpectedVersion: expected,
                ExpectedDraftVersion: Draft(services).Version), "original", null, true, token: TestContext.Current.CancellationToken);

    [Fact]
    public async Task ExportAcrossIndependentHostsRemapsReferencesAndCommitsOnlyDraftWithRefillRequirement()
    {
        await using var source = new TenonEdgeApplicationFactory();
        var output = Connection(source.Services);
        await Credential(source.Services, output);
        var device = source.Services.GetRequiredService<DeviceDraftStore>().Write(null,
            new(output.Version, "meter", "Meter", DeviceKind.Simulator), "original", null);
        var input = source.Services.GetRequiredService<InputDraftStore>().Write(device.Device.Id, null,
            new(device.Version, "values", "Values", true, Simulator: new(SampleJson: "{\"value\":9223372036854775807}")), "original", null);
        var file = Export(source.Services) with
        {
            Routes = [new("forward", "Forward", null, true, new(1,
                [new("in", "input.simulator", 1, "Input", JsonSerializer.Deserialize<JsonElement>("""{"sourceKeys":["meter/values"]}""")),
                 new("out", "output.http", 1, "Output", JsonSerializer.Deserialize<JsonElement>("""{"connectionKey":"receiver","path":"ingest","payload":{"mode":"values"}}"""))],
                [new("in", "out")], new([])))],
        };
        Assert.Equal(input.Input.Id, Assert.Single(Draft(source.Services).Inputs).Id);
        await using var target = new TenonEdgeApplicationFactory();
        var untouched = Connection(target.Services, "untouched");
        var before = Draft(target.Services);
        var store = Store(target.Services);
        var candidate = store.Prepare(Bytes(file), ConfigurationImportMode.Merge, before.Version, Token);
        Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(Draft(target.Services)));
        var result = store.Commit(candidate, Actor(target.Services), Token);
        var after = Draft(target.Services);
        Assert.Equal(result.Version, after.Version);
        Assert.NotEqual(before.Version, after.Version);
        Assert.Equal(2, after.Connections.Length);
        Assert.Equal(JsonSerializer.Serialize(untouched.Connection), JsonSerializer.Serialize(after.Connections.Single(value => value.Key == "untouched")));
        Assert.Equal(before.Edits!.Single(), after.Edits!.Single(value => value.Id == untouched.Connection.Id));
        var connection = after.Connections.Single(value => value.Key == "receiver");
        Assert.Equal(Assert.Single(after.Devices).Id, Assert.Single(after.Inputs).DeviceId);
        var graph = Assert.Single(after.Routes).Graph;
        Assert.Equal(after.Inputs[0].Id, graph.Nodes[0].Config.GetProperty("sourceIds")[0].GetInt64());
        Assert.Equal(connection.Id, graph.Nodes[1].Config.GetProperty("connectionId").GetInt64());
        Assert.Equal("{\"value\":9223372036854775807}", after.Inputs[0].Simulator!.SampleJson);
        Assert.Equal("importer", after.Routes[0].UpdatedBy);
        Assert.Equal(connection.Id, Assert.Single(after.RequiredCredentials!).ConnectionId);
        Assert.Null(target.Services.GetRequiredService<HttpCredentialStore>().Read(connection.Id));
        Assert.Equal("configuration.credentialsRequired", Assert.Throws<DraftWriteException>(() =>
            target.Services.GetRequiredService<RuntimeVersionStore>().ReviewDraft(result.Version, Token)).Code);
        var db = Database(target.Services);
        Assert.Equal(2, db.Client.Queryable<RouteDraftReferenceRecord>().Count());
        Assert.Equal(1, db.Client.Ado.GetInt("SELECT COUNT(*) FROM audit_log WHERE action='configuration.imported'"));
        var state = db.Client.Queryable<SystemStateRecord>().InSingle(1);
        Assert.Null(state.EffectiveRevision);
        Assert.Null(state.PendingRevision);
    }

    [Fact]
    public async Task MatchingIdentityKeepsCredentialButRecipientChangeAddsPersistedGuardWithoutRebindingSecret()
    {
        using var data = TemporaryDirectory.Create();
        long id;
        string version;
        await using (var host = new FixedDataDirectoryApplicationFactory(data.Path))
        {
            var connection = Connection(host.Services);
            id = connection.Connection.Id;
            var credential = await Credential(host.Services, connection);
            var store = Store(host.Services);
            var file = Export(host.Services);
            var same = store.Prepare(Bytes(file), ConfigurationImportMode.Merge, Draft(host.Services).Version, Token);
            Assert.Equal("preserved", Assert.Single(same.Credentials).Action);
            Assert.Empty(same.Changes);
            store.Commit(same, Actor(host.Services), Token);
            Assert.Empty(Draft(host.Services).RequiredCredentials!);
            file.Connections[0] = file.Connections[0] with { Http = new("https://other.example.com/base/") };
            var changed = store.Prepare(Bytes(file), ConfigurationImportMode.Merge, Draft(host.Services).Version, Token);
            Assert.Equal("recipientChanged", Assert.Single(changed.Credentials).Reason);
            Assert.Equal("modified", Assert.Single(changed.Changes).Change);
            version = store.Commit(changed, Actor(host.Services), Token).Version;
            Assert.Equal(credential, host.Services.GetRequiredService<HttpCredentialStore>().Read(id));
            Assert.Equal("fixture-secret", host.Services.GetRequiredService<HttpCredentialStore>().ResolveHttp(id, "https://example.com/base/")!.Secret);
        }
        await using (var restarted = new FixedDataDirectoryApplicationFactory(data.Path))
        {
            Assert.Equal(version, Draft(restarted.Services).Version);
            Assert.Equal(id, Assert.Single(Draft(restarted.Services).RequiredCredentials!).ConnectionId);
            Assert.Equal("https://other.example.com/base/", Draft(restarted.Services).Connections[0].Http!.BaseAddress);
            Assert.Equal("fixture-secret", restarted.Services.GetRequiredService<HttpCredentialStore>().ResolveHttp(id, "https://example.com/base/")!.Secret);
        }
    }

    [Fact]
    public async Task ReplacementDeletesDraftObjectsButPreservesCurrentCredentialsAndRetainedEditTombstones()
    {
        await using var host = new TenonEdgeApplicationFactory();
        var connection = Connection(host.Services);
        var credential = await Credential(host.Services, connection);
        var before = Draft(host.Services);
        Database(host.Services).InTransaction(client => { ConfigurationEditJournal.Retain(client, before.Edits); return true; });
        var store = Store(host.Services);
        var candidate = store.Prepare(Bytes(Empty), ConfigurationImportMode.Replace, before.Version, Token);
        Assert.Equal("removed", Assert.Single(candidate.Changes).Change);
        store.Commit(candidate, Actor(host.Services), Token);
        var after = Draft(host.Services);
        Assert.Empty(after.Connections);
        Assert.Empty(after.RequiredCredentials!);
        var edit = Assert.Single(after.Edits!);
        Assert.True(edit.Deleted);
        Assert.Equal("importer", edit.UpdatedBy);
        Assert.Equal(credential, host.Services.GetRequiredService<HttpCredentialStore>().Read(connection.Connection.Id));
        Assert.Equal("fixture-secret", host.Services.GetRequiredService<HttpCredentialStore>().ResolveHttp(connection.Connection.Id, credential.Recipient)!.Secret);
        Assert.True(Database(host.Services).Client.Queryable<SystemStateRecord>().InSingle(1).IdFloor >= connection.Connection.Id);
    }

    [Theory]
    [InlineData("draft")]
    [InlineData("rotation")]
    [InlineData("credentialAdded")]
    public async Task ConcurrentChangesRejectConfirmationWithoutOverwritingAnything(string kind)
    {
        await using var host = new TenonEdgeApplicationFactory();
        var connection = Connection(host.Services);
        var credential = kind == "credentialAdded" ? null : await Credential(host.Services, connection);
        var store = Store(host.Services);
        var candidate = store.Prepare(Bytes(Export(host.Services)), ConfigurationImportMode.Replace, connection.Version, Token);
        if (kind == "draft")
        { Connection(host.Services, "newer"); }
        else
        { await Credential(host.Services, connection, credential?.Version, "fixture-rotated"); }
        var before = JsonSerializer.Serialize(Draft(host.Services));
        var currentCredential = host.Services.GetRequiredService<HttpCredentialStore>().Read(connection.Connection.Id);
        var error = Assert.Throws<DraftWriteException>(() => store.Commit(candidate, Actor(host.Services), Token));
        Assert.Equal(kind == "draft" ? "configuration.draftConflict" : "configuration.importCredentialsChanged", error.Code);
        Assert.Equal(before, JsonSerializer.Serialize(Draft(host.Services)));
        Assert.Equal(currentCredential, host.Services.GetRequiredService<HttpCredentialStore>().Read(connection.Connection.Id));
        Assert.Equal(0, Database(host.Services).Client.Ado.GetInt("SELECT COUNT(*) FROM audit_log WHERE action='configuration.imported'"));
    }

    [Fact]
    public async Task AuditFailureRollsBackReplacementRequirementsVersionFloorAndEditJournal()
    {
        await using var host = new TenonEdgeApplicationFactory();
        var connection = Connection(host.Services);
        await Credential(host.Services, connection);
        var file = Export(host.Services);
        file.Connections[0] = file.Connections[0] with { ConnectionKey = "new-receiver" };
        var store = Store(host.Services);
        var candidate = store.Prepare(Bytes(file), ConfigurationImportMode.Replace, connection.Version, Token);
        var db = Database(host.Services);
        var before = JsonSerializer.Serialize(Draft(host.Services));
        var state = JsonSerializer.Serialize(db.Client.Queryable<SystemStateRecord>().InSingle(1));
        db.Client.Ado.ExecuteCommand("CREATE TRIGGER reject_import_audit BEFORE INSERT ON audit_log WHEN NEW.action='configuration.imported' BEGIN SELECT RAISE(ABORT, 'fixture'); END;");
        Assert.ThrowsAny<Exception>(() => store.Commit(candidate, Actor(host.Services), Token));
        Assert.Equal(before, JsonSerializer.Serialize(Draft(host.Services)));
        Assert.Equal(state, JsonSerializer.Serialize(db.Client.Queryable<SystemStateRecord>().InSingle(1)));
        Assert.Equal("fixture-secret", host.Services.GetRequiredService<HttpCredentialStore>().ResolveHttp(connection.Connection.Id, "https://example.com/base/")!.Secret);
    }

    [Fact]
    public async Task ReservedCredentialIdentityAppearingAfterPreviewCannotBeReused()
    {
        await using var host = new TenonEdgeApplicationFactory();
        var connection = Connection(host.Services);
        await Credential(host.Services, connection);
        var file = Export(host.Services);
        file.Connections[0] = file.Connections[0] with { ConnectionKey = "new-receiver" };
        var store = Store(host.Services);
        var candidate = store.Prepare(Bytes(file), ConfigurationImportMode.Merge, connection.Version, Token);
        var newId = Assert.Single(candidate.NewConnectionIds);
        var db = Database(host.Services);
        var existing = db.Client.Queryable<HttpCredentialRecord>().InSingle(connection.Connection.Id);
        existing.ConnectionId = newId;
        db.Client.Insertable(existing).ExecuteCommand(); // Explicit collision fixture, not a valid API operation.
        var before = JsonSerializer.Serialize(Draft(host.Services));
        Assert.Equal("connection.reservedIdentity", Assert.Throws<DraftWriteException>(() => store.Commit(candidate, Actor(host.Services), Token)).Code);
        Assert.Equal(before, JsonSerializer.Serialize(Draft(host.Services)));
    }

    [Fact]
    public async Task SmallValidUploadCannotGrowMergedPersistentDraftBeyondItsBudget()
    {
        await using var host = new TenonEdgeApplicationFactory();
        var device = host.Services.GetRequiredService<DeviceDraftStore>().Write(null,
            new("0", "meter", "Meter", DeviceKind.Simulator), "original", null);
        var inputStore = host.Services.GetRequiredService<InputDraftStore>();
        var version = device.Version;
        var sample = "{\"value\":\"" + new string('a', 3 * 1024 * 1024) + "\"}";
        for (var index = 0; index < 5; index++)
        {
            var saved = inputStore.Write(device.Device.Id, null, new(version, "source" + index, "Source", false,
                Simulator: new(SampleJson: sample.Replace("value", "value" + index, StringComparison.Ordinal))), "original", null);
            version = saved.Version;
        }
        var before = Draft(host.Services);
        var smallFile = Empty with
        {
            Inputs = [new("meter", "extra", "Extra", DeviceKind.Simulator, false, null, null, null,
                new(SampleJson: "{\"extra\":\"" + new string('b', 2 * 1024 * 1024) + "\"}"))],
        };
        var bytes = Bytes(smallFile);
        Assert.True(bytes.Length < ConfigurationExport.MaximumBytes);
        var error = Assert.Throws<ConfigurationImportException>(() => Store(host.Services).Prepare(bytes, ConfigurationImportMode.Merge, version, Token));
        Assert.Equal("import.draftTooLarge", Assert.Single(error.Issues).Code);
        Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(Draft(host.Services)));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RetainedSeedDeviceAndInputKeysCannotBeReintroducedWithNewIds(bool existingDevice, bool afterPreview)
    {
        await using var host = new TenonEdgeApplicationFactory();
        if (existingDevice)
        {
            host.Services.GetRequiredService<DeviceDraftStore>().Write(null,
                new("0", "meter", "Meter", DeviceKind.Simulator), "original", null);
        }
        var version = Draft(host.Services).Version;
        var file = Empty with
        {
            Devices = existingDevice ? [] : [new("meter", "Meter", DeviceKind.Simulator, null, null, null, ModbusAddressNotation.Offset, true)],
            Inputs = [new("meter", "values", "Values", DeviceKind.Simulator, false, null, null, null, new())],
        };
        var store = Store(host.Services);
        var candidate = afterPreview ? store.Prepare(Bytes(file), ConfigurationImportMode.Merge, version, Token) : null;
        var versions = host.Services.GetRequiredService<RuntimeVersionStore>();
        versions.Freeze(RuntimePlanDocument.Parse("""{"schemaVersion":1,"units":[{"kind":"input.simulator","key":"meter/values","configuration":{"periodMs":5000}}]}"""u8),
            RuntimeSnapshotOrigins.Seed, null, "fixture");
        var before = JsonSerializer.Serialize(Draft(host.Services));
        var pointers = versions.ReadPointers();
        var error = afterPreview
            ? Assert.Throws<DraftWriteException>(() => store.Commit(candidate!, Actor(host.Services), Token))
            : Assert.Throws<DraftWriteException>(() => store.Prepare(Bytes(file), ConfigurationImportMode.Merge, version, Token));
        Assert.Equal(existingDevice ? "input.reservedKey" : "device.reservedKey", error.Code);
        Assert.Equal(before, JsonSerializer.Serialize(Draft(host.Services)));
        Assert.Equal(pointers, versions.ReadPointers());
    }

    [Fact]
    public async Task FinalSizeGuardRollsBackWhenPersistedJournalOutgrowsPreview()
    {
        await using var host = new TenonEdgeApplicationFactory();
        var connection = Connection(host.Services);
        var store = Store(host.Services);
        var candidate = store.Prepare(Bytes(Empty), ConfigurationImportMode.Replace, connection.Version, Token);
        var db = Database(host.Services);
        // Deliberate corrupt/out-of-band growth fixture verifies the final SQL
        // guard independently of the preview estimator; no public API does this.
        db.Client.Ado.ExecuteCommand("INSERT INTO configuration_edit(kind,object_id,stamp,updated_by,updated_at,deleted,retained) VALUES ('device',123,'fixture',@oversize,0,1,1)",
            new SqlSugar.SugarParameter("@oversize", new string('a', 17 * 1024 * 1024)));
        var floor = db.Client.Queryable<SystemStateRecord>().InSingle(1).IdFloor;
        Assert.Throws<RuntimePlanDocumentException>(() => store.Commit(candidate, Actor(host.Services), Token));
        Assert.Equal(connection.Version, db.InTransaction(ConfigurationDraft.ReadVersion));
        Assert.Equal(connection.Connection.Id, db.Client.Queryable<ConnectionDraftRecord>().Single().Id);
        Assert.Equal(floor, db.Client.Queryable<SystemStateRecord>().InSingle(1).IdFloor);
        Assert.Equal(0, db.Client.Ado.GetInt("SELECT COUNT(*) FROM audit_log WHERE action='configuration.imported'"));
    }

    [Fact]
    public async Task ReimportCannotEraseAnExistingRequiredCredentialByOmittingItsHint()
    {
        await using var source = new TenonEdgeApplicationFactory();
        var connection = Connection(source.Services);
        await Credential(source.Services, connection);
        var file = Export(source.Services);
        await using var target = new TenonEdgeApplicationFactory();
        var store = Store(target.Services);
        store.Commit(store.Prepare(Bytes(file), ConfigurationImportMode.Merge, "0", Token), Actor(target.Services), Token);
        var original = Assert.Single(Draft(target.Services).RequiredCredentials!);
        file.Connections[0] = file.Connections[0] with { Credential = null };
        var candidate = store.Prepare(Bytes(file), ConfigurationImportMode.Replace, Draft(target.Services).Version, Token);
        Assert.Equal("alreadyRequiresInput", Assert.Single(candidate.Credentials).Reason);
        store.Commit(candidate, Actor(target.Services), Token);
        Assert.Equal(original.AccountIdentity, Assert.Single(Draft(target.Services).RequiredCredentials!).AccountIdentity);
        Assert.Equal("configuration.credentialsRequired", Assert.Throws<DraftWriteException>(() =>
            target.Services.GetRequiredService<RuntimeVersionStore>().ReviewDraft(Draft(target.Services).Version, Token)).Code);
    }

    [Fact]
    public async Task InvalidFileAndCancellationNeverWritePartialDrafts()
    {
        await using var host = new TenonEdgeApplicationFactory();
        Connection(host.Services);
        var before = JsonSerializer.Serialize(Draft(host.Services));
        var store = Store(host.Services);
        Assert.Throws<ConfigurationImportException>(() => store.Prepare("{\"formatVersion\":9}"u8.ToArray(), ConfigurationImportMode.Merge, Draft(host.Services).Version, Token));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => store.Prepare(Bytes(Empty), ConfigurationImportMode.Replace, Draft(host.Services).Version, cancelled.Token));
        var candidate = store.Prepare(Bytes(Empty), ConfigurationImportMode.Replace, Draft(host.Services).Version, Token);
        Assert.Throws<OperationCanceledException>(() => store.Commit(candidate, Actor(host.Services), cancelled.Token));
        Assert.Equal(before, JsonSerializer.Serialize(Draft(host.Services)));
    }
}
