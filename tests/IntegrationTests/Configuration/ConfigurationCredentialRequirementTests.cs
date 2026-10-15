using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Host.Features.Configuration;
using TenonEdge.Host.Features.Configuration.Transfer;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Credentials;
using TenonEdge.Host.Features.Routes;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Storage;
using TenonEdge.IntegrationTests.Infrastructure;

namespace TenonEdge.IntegrationTests.Configuration;

public sealed class ConfigurationCredentialRequirementTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly string RequiredIdentity = "sha256:" + new string('b', 64);
    // SQL-side setup is an explicit fixture for the protection boundary. There is
    // not yet an import API, so this is not an import end-to-end acceptance test.
    private static string Require(IServiceProvider services, long id) => services.GetRequiredService<ConfigDatabase>().InTransaction(client =>
    {
        ConfigurationCredentialRequirements.Replace(client, [new(id, RequiredIdentity, "exportRequiresInput")]);
        ConfigurationEditJournal.Record(client, "connection", id, "fixture", TimeProvider.System);
        return ConfigurationDraft.Advance(client, TimeProvider.System);
    });
    private static ConfigurationCheckpoint Draft(IServiceProvider services) => services.GetRequiredService<ConfigDatabase>()
        .InTransaction(client => ConfigurationCheckpoint.Read(client, ConfigurationDraft.ReadVersion(client)));
    private static ConnectionDraftResult Connection(IServiceProvider services, bool mqtt = false) => services.GetRequiredService<ConnectionDraftStore>()
        .Write(null, mqtt ? new("0", "target", "Target", ConnectionKind.Mqtt, Mqtt: new("broker.example.com"))
            : new("0", "target", "Target", ConnectionKind.Http, Http: new("https://example.com/")), "fixture", null);

    [Fact]
    public async Task MissingCredentialRequirementIsDurableExportableAndBlocksCompilationReviewAndFreeze()
    {
        using var data = TemporaryDirectory.Create();
        long id;
        string version;
        await using (var host = new FixedDataDirectoryApplicationFactory(data.Path))
        {
            var saved = Connection(host.Services);
            id = saved.Connection.Id;
            var before = Draft(host.Services);
            version = Require(host.Services, id);
            var flagged = Draft(host.Services);
            var required = Assert.Single(flagged.RequiredCredentials!);
            Assert.Equal(RequiredIdentity, required.AccountIdentity);
            var change = Assert.Single(ConfigurationDiff.Between(before, flagged));
            Assert.Equal("connection", change.Kind);
            Assert.Equal("modified", change.Change);
            Assert.Equal(RequiredIdentity, change.After!.Value.GetProperty("credentialRequirement").GetProperty("accountIdentity").GetString());
            Assert.Null(host.Services.GetRequiredService<HttpCredentialStore>().Read(id));
        }
        await using (var host = new FixedDataDirectoryApplicationFactory(data.Path))
        {
            Assert.Equal(version, Draft(host.Services).Version);
            Assert.Equal(id, Assert.Single(Draft(host.Services).RequiredCredentials!).ConnectionId);
            using var exported = JsonDocument.Parse(host.Services.GetRequiredService<ConfigurationExportStore>().Export(version, Token));
            Assert.Equal(RequiredIdentity, exported.RootElement.GetProperty("connections")[0].GetProperty("credential").GetProperty("accountIdentity").GetString());
            var versions = host.Services.GetRequiredService<RuntimeVersionStore>();
            Assert.Equal("configuration.credentialsRequired", Assert.Throws<DraftWriteException>(() => versions.ReviewDraft(version, Token)).Code);
            Assert.Equal("configuration.credentialsRequired", Assert.Throws<DraftWriteException>(() => host.Services.GetRequiredService<RouteDraftStore>().ReadCompilation(version, Token)).Code);
            var empty = RuntimePlanDocument.Parse("""{"schemaVersion":1,"units":[]}"""u8);
            Assert.Equal("configuration.credentialsRequired", Assert.Throws<DraftWriteException>(() => versions.FreezeDraft(empty, version, "fixture")).Code);
            Assert.Null(host.Services.GetRequiredService<ConfigDatabase>().Client.Queryable<SystemStateRecord>().InSingle(1).PendingRevision);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RequirementDoesNotTouchCurrentCredentialAndOnlyExplicitNewSecretCompletesIt(bool mqtt)
    {
        await using var host = new TenonEdgeApplicationFactory();
        var saved = Connection(host.Services, mqtt);
        var id = saved.Connection.Id;
        var http = host.Services.GetRequiredService<HttpCredentialStore>();
        var broker = host.Services.GetRequiredService<MqttCredentialStore>();
        var first = mqtt
            ? (await broker.WriteAsync(id, new("mqtt://broker.example.com:1883", Username: "fixture-user", Password: "fixture-secret", ExpectedDraftVersion: saved.Version), "fixture", null, true, token: TestContext.Current.CancellationToken)).Version
            : (await http.WriteAsync(id, new("https://example.com/", HttpCredentialKind.Bearer, Secret: "fixture-secret", ExpectedDraftVersion: saved.Version), "fixture", null, true, token: TestContext.Current.CancellationToken)).Version;
        var version = Require(host.Services, id);
        if (mqtt)
        { Assert.Equal("fixture-secret", broker.ResolveMqtt(id, "mqtt://broker.example.com:1883")!.Password); }
        else
        { Assert.Equal("fixture-secret", http.ResolveHttp(id, "https://example.com/")!.Secret); }
        // Administrator retain is not an imported-secret refill.
        var retained = mqtt
            ? (await broker.WriteAsync(id, new("mqtt://broker.example.com:1883", ExpectedVersion: first, RetainExisting: true, ExpectedDraftVersion: version), "fixture", null, true, token: TestContext.Current.CancellationToken)).Version
            : (await http.WriteAsync(id, new("https://example.com/", ExpectedVersion: first, RetainExisting: true, ExpectedDraftVersion: version), "fixture", null, true, token: TestContext.Current.CancellationToken)).Version;
        Assert.Single(Draft(host.Services).RequiredCredentials!);
        Assert.Equal(version, Draft(host.Services).Version);
        if (mqtt)
        {
            await broker.WriteAsync(id, new("mqtt://broker.example.com:1883", Username: "fixture-user", Password: "fixture-new-secret", ExpectedVersion: retained,
                ExpectedDraftVersion: version, AccountChanged: false), "fixture", null, true, token: TestContext.Current.CancellationToken);
        }
        else
        {
            await http.WriteAsync(id, new("https://example.com/", HttpCredentialKind.Bearer, Secret: "fixture-new-secret", ExpectedVersion: retained,
                ExpectedDraftVersion: version, AccountChanged: false), "fixture", null, true, token: TestContext.Current.CancellationToken);
        }
        Assert.Empty(Draft(host.Services).RequiredCredentials!);
        Assert.NotEqual(version, Draft(host.Services).Version);
        host.Services.GetRequiredService<ConfigDatabase>().InTransaction(client => { ConfigurationCredentialRequirements.RequireComplete(client); return true; });
    }

    [Fact]
    public async Task CredentialAuditFailureRollsBackSecretRequirementDraftVersionAndEditStampTogether()
    {
        await using var host = new TenonEdgeApplicationFactory();
        var saved = Connection(host.Services);
        var store = host.Services.GetRequiredService<HttpCredentialStore>();
        var first = await store.WriteAsync(saved.Connection.Id, new("https://example.com/", HttpCredentialKind.Bearer,
            Secret: "fixture-old-secret", ExpectedDraftVersion: saved.Version), "fixture", null, true, token: TestContext.Current.CancellationToken);
        var version = Require(host.Services, saved.Connection.Id);
        var before = JsonSerializer.Serialize(Draft(host.Services));
        var db = host.Services.GetRequiredService<ConfigDatabase>();
        db.Client.Ado.ExecuteCommand("CREATE TRIGGER reject_refill_audit BEFORE INSERT ON audit_log WHEN NEW.action = 'credential.http.write' BEGIN SELECT RAISE(ABORT, 'fixture'); END;");
        await Assert.ThrowsAnyAsync<Exception>(() => store.WriteAsync(saved.Connection.Id, new("https://example.com/", HttpCredentialKind.Bearer,
            Secret: "fixture-new-secret", ExpectedVersion: first.Version, ExpectedDraftVersion: version), "fixture", null, true, token: TestContext.Current.CancellationToken));
        Assert.Equal(before, JsonSerializer.Serialize(Draft(host.Services)));
        Assert.Equal(first, store.Read(saved.Connection.Id));
        Assert.Equal("fixture-old-secret", store.ResolveHttp(saved.Connection.Id, "https://example.com/")!.Secret);
    }

    [Fact]
    public async Task DraftReplacementRestoresRequirementsAndNeverChangesTheCurrentCredential()
    {
        await using var host = new TenonEdgeApplicationFactory();
        var saved = Connection(host.Services);
        var original = Draft(host.Services);
        var store = host.Services.GetRequiredService<HttpCredentialStore>();
        await store.WriteAsync(saved.Connection.Id, new("https://example.com/", HttpCredentialKind.Bearer, Secret: "fixture-secret", ExpectedDraftVersion: saved.Version), "fixture", null, true, token: TestContext.Current.CancellationToken);
        var version = Require(host.Services, saved.Connection.Id);
        var flagged = Draft(host.Services);
        var db = host.Services.GetRequiredService<ConfigDatabase>();
        Assert.True(db.InTransaction(client => new ConfigurationReplacement(original).Apply(client, version, TimeProvider.System)));
        Assert.Empty(Draft(host.Services).RequiredCredentials!);
        Assert.Equal("fixture-secret", store.ResolveHttp(saved.Connection.Id, "https://example.com/")!.Secret);
        var reset = Draft(host.Services).Version;
        Assert.True(db.InTransaction(client => new ConfigurationReplacement(flagged).Apply(client, reset, TimeProvider.System)));
        Assert.Equal(RequiredIdentity, Assert.Single(Draft(host.Services).RequiredCredentials!).AccountIdentity);
        Assert.Equal("fixture-secret", store.ResolveHttp(saved.Connection.Id, "https://example.com/")!.Secret);
    }

    [Fact]
    public async Task ClearingAnUnreferencedCurrentCredentialCannotTurnImportedRequirementIntoAnonymousAccess()
    {
        await using var host = new TenonEdgeApplicationFactory();
        var saved = Connection(host.Services);
        var store = host.Services.GetRequiredService<HttpCredentialStore>();
        var credential = await store.WriteAsync(saved.Connection.Id, new("https://example.com/", HttpCredentialKind.Bearer,
            Secret: "fixture-secret", ExpectedDraftVersion: saved.Version), "fixture", null, true, token: TestContext.Current.CancellationToken);
        var version = Require(host.Services, saved.Connection.Id);
        store.Clear(saved.Connection.Id, version, credential.Version, "fixture", null);
        Assert.Null(store.Read(saved.Connection.Id));
        Assert.Single(Draft(host.Services).RequiredCredentials!);
        Assert.Equal(version, Draft(host.Services).Version);
        Assert.Throws<DraftWriteException>(() => host.Services.GetRequiredService<RuntimeVersionStore>().ReviewDraft(version, Token));
        using var exported = JsonDocument.Parse(host.Services.GetRequiredService<ConfigurationExportStore>().Export(version, Token));
        Assert.Equal("requiresInput", exported.RootElement.GetProperty("connections")[0].GetProperty("credential").GetProperty("status").GetString());
    }

    [Fact]
    public async Task RequirementCannotAllocateOrReserveAnIndependentConnectionIdentity()
    {
        await using var host = new TenonEdgeApplicationFactory();
        var saved = Connection(host.Services);
        Require(host.Services, saved.Connection.Id);
        var before = JsonSerializer.Serialize(Draft(host.Services));
        var db = host.Services.GetRequiredService<ConfigDatabase>();
        Assert.Throws<ArgumentException>(() => db.InTransaction(client =>
        {
            ConfigurationCredentialRequirements.Replace(client, [new(1, RequiredIdentity, "fixture")]);
            return true;
        }));
        Assert.Equal(before, JsonSerializer.Serialize(Draft(host.Services)));
    }

    [Fact]
    public async Task DeletingAnUnreferencedDraftRemovesOnlyItsOwnRequirement()
    {
        await using var host = new TenonEdgeApplicationFactory();
        var saved = Connection(host.Services);
        var version = Require(host.Services, saved.Connection.Id);
        host.Services.GetRequiredService<ConnectionDraftStore>().Delete(saved.Connection.Id, version, "fixture", null);
        Assert.Empty(Draft(host.Services).RequiredCredentials!);
        Assert.Equal(0, host.Services.GetRequiredService<ConfigDatabase>().Client.Queryable<CredentialRequirementRecord>().Count());
    }
}
