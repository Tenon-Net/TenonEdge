using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Host.Features.Audit;
using TenonEdge.Host.Features.Configuration;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Credentials;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Security;
using TenonEdge.Host.Storage;
using TenonEdge.Host.Storage.Ids;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.Runtime.Deliveries;

namespace TenonEdge.IntegrationTests.Credentials;

public sealed class DraftCredentialTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private const string Secret = "draft-fixture-secret-not-real";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManagedCredentialsRequireDraftVersionProtocolAndRecipientInTheWriteTransaction(bool mqtt)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        using var client = await Admin(factory);
        var draft = CreateDraft(factory, mqtt);
        using var missing = await Put(client, draft, mqtt, null);
        Assert.Equal("credential.draftVersionRequired", await missing.ReadProblemCodeAsync(Token));
        using var stale = await Put(client, draft, mqtt, "stale");
        Assert.Equal("credential.draftConflict", await stale.ReadProblemCodeAsync(Token));
        using var wrongRecipient = await Put(client, draft, mqtt, draft.Version, recipient: mqtt ? "mqtt://10.0.0.2:1883" : "http://10.0.0.2/");
        Assert.Equal("credential.recipientMismatch", await wrongRecipient.ReadProblemCodeAsync(Token));
        using var wrongProtocol = await Put(client, draft, !mqtt, draft.Version);
        Assert.Equal("credential.protocolConflict", await wrongProtocol.ReadProblemCodeAsync(Token));
        using var saved = await Put(client, draft, mqtt, draft.Version);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.DoesNotContain(Secret, await saved.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);
        var metadata = await saved.ReadJsonAsync(Token);
        var version = metadata.GetProperty("version").GetString()!;
        Assert.True(metadata.GetProperty("configured").GetBoolean());
        Assert.Equal(draft.Version, factory.Services.GetRequiredService<ConnectionDraftStore>().Read().Version);
        using var status = await client.GetAsync(Path(draft.Connection.Id, mqtt), Token);
        Assert.DoesNotContain(Secret, await status.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);
        var database = factory.Services.GetRequiredService<ConfigDatabase>();
        Assert.DoesNotContain(Secret, database.Client.Queryable<ConnectionDraftRecord>().InSingle(draft.Connection.Id).Content, StringComparison.Ordinal);
        using var clearStale = await Clear(client, draft, mqtt, "stale");
        Assert.Equal("credential.versionConflict", await clearStale.ReadProblemCodeAsync(Token));
        using var cleared = await Clear(client, draft, mqtt, version);
        Assert.Equal(HttpStatusCode.NoContent, cleared.StatusCode);
        Assert.Equal(draft.Version, factory.Services.GetRequiredService<ConnectionDraftStore>().Read().Version);
        using var empty = await client.GetAsync(Path(draft.Connection.Id, mqtt), Token);
        Assert.Equal(HttpStatusCode.NotFound, empty.StatusCode);
        Assert.Single(TestAccounts.Audit(factory.Services, mqtt ? "credential.mqtt.clear" : "credential.http.clear"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnEditAfterOpeningTheCredentialFormRejectsTheOldBinding(bool mqtt)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        using var client = await Admin(factory);
        var draft = CreateDraft(factory, mqtt);
        var store = factory.Services.GetRequiredService<ConnectionDraftStore>();
        var connection = draft.Connection;
        store.Write(connection.Id, new(draft.Version, connection.Key, "changed", connection.Kind, connection.Enabled,
            connection.Modbus, connection.Mqtt, connection.Http), "admin", null);
        using var rejected = await Put(client, draft, mqtt, draft.Version);
        Assert.Equal("credential.draftConflict", await rejected.ReadProblemCodeAsync(Token));
        using var empty = await client.GetAsync(Path(connection.Id, mqtt), Token);
        Assert.Equal(HttpStatusCode.NotFound, empty.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeletedDraftCannotBeReboundByAStaleManagedForm(bool mqtt)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        using var client = await Admin(factory);
        var draft = CreateDraft(factory, mqtt);
        using var saved = await Put(client, draft, mqtt, draft.Version);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        factory.Services.GetRequiredService<ConnectionDraftStore>().Delete(draft.Connection.Id, draft.Version, "admin", null);
        using var absent = await client.GetAsync(Path(draft.Connection.Id, mqtt), Token);
        Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode);
        Assert.Single(TestAccounts.Audit(factory.Services, "credential.connectionDeleted"));
        using var rejected = await Put(client, draft, mqtt, draft.Version);
        Assert.Equal("credential.connectionNotFound", await rejected.ReadProblemCodeAsync(Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClearRequiresWriteRoleCsrfAndSuccessfulAudit(bool mqtt)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        using var admin = await Admin(factory);
        var draft = CreateDraft(factory, mqtt);
        using var saved = await Put(admin, draft, mqtt, draft.Version);
        var version = (await saved.ReadJsonAsync(Token)).GetProperty("version").GetString()!;
        TestAccounts.Create(factory.Services, "viewer", ApiRequests.StrongPassword, UserRole.Viewer);
        using var viewer = await factory.CreateLoggedInClientAsync("viewer", Token);
        using var denied = await Clear(viewer, draft, mqtt, version);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        admin.DefaultRequestHeaders.Remove(ApiRequests.CsrfHeader);
        using var csrf = await Clear(admin, draft, mqtt, version);
        Assert.Equal(HttpStatusCode.Forbidden, csrf.StatusCode);
        var action = mqtt ? "credential.mqtt.clear" : "credential.http.clear";
        factory.Services.GetRequiredService<ConfigDatabase>().Client.Ado.ExecuteCommand(
            $"CREATE TRIGGER fail_clear BEFORE INSERT ON audit_log WHEN NEW.action = '{action}' BEGIN SELECT RAISE(ABORT, 'fixture'); END");
        if (mqtt)
        {
            var store = factory.Services.GetRequiredService<MqttCredentialStore>();
            Assert.ThrowsAny<Exception>(() => store.Clear(draft.Connection.Id, draft.Version, version, "admin", null));
            Assert.Equal(Secret, store.ResolveMqtt(draft.Connection.Id, "mqtt://10.0.0.1:1883")!.Password);
        }
        else
        {
            var store = factory.Services.GetRequiredService<HttpCredentialStore>();
            Assert.ThrowsAny<Exception>(() => store.Clear(draft.Connection.Id, draft.Version, version, "admin", null));
            Assert.Equal(Secret, store.ResolveHttp(draft.Connection.Id, "http://10.0.0.1/")!.Secret);
        }
        Assert.Empty(TestAccounts.Audit(factory.Services, action));
    }

    [Theory]
    [InlineData("effective", false)]
    [InlineData("effective", true)]
    [InlineData("previous", false)]
    [InlineData("previous", true)]
    [InlineData("pending", false)]
    [InlineData("pending", true)]
    public async Task RetainedVersionReferencesPreventClearing(string referenceKind, bool textId)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        using var admin = await Admin(factory);
        var draft = CreateDraft(factory, false);
        using var saved = await Put(admin, draft, false, draft.Version);
        var version = (await saved.ReadJsonAsync(Token)).GetProperty("version").GetString()!;
        var connectionId = textId ? JsonSerializer.Serialize(draft.Connection.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)) : JsonSerializer.Serialize(draft.Connection.Id);
        var json = "{\"schemaVersion\":1,\"units\":[{\"kind\":\"output.http\",\"key\":\"out\",\"configuration\":{\"connectionId\":" + connectionId + ",\"baseAddress\":\"http://10.0.0.1/\",\"inputs\":[\"meter/input\"]}}]}";
        var plan = RuntimePlanDocument.Parse(Encoding.UTF8.GetBytes(json));
        factory.Services.GetRequiredService<ConfigDatabase>().InTransaction(db =>
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
        using var rejected = await Clear(admin, draft, false, version);
        Assert.Equal("credential.connectionReferenced", await rejected.ReadProblemCodeAsync(Token));
        Assert.Equal(Secret, factory.Services.GetRequiredService<HttpCredentialStore>().ResolveHttp(draft.Connection.Id, "http://10.0.0.1/")!.Secret);
    }

    [Fact]
    public async Task ChangingApiKeyParameterNameStillRequiresAnAccountDeclarationForUndeliveredRecords()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        using var admin = await Admin(factory);
        var draft = CreateDraft(factory, false);
        var store = factory.Services.GetRequiredService<HttpCredentialStore>();
        var initial = await store.WriteAsync(draft.Connection.Id, new("http://10.0.0.1/", HttpCredentialKind.ApiKeyHeader,
            Name: "X-Key-One", Secret: Secret, ExpectedDraftVersion: draft.Version), "admin", null, true, token: TestContext.Current.CancellationToken);
        var cache = factory.Services.GetRequiredService<DeliveryCache>();
        Assert.True((await cache.EnqueueAsync([new DeliveryDraft(Guid.NewGuid(), draft.Connection.Id, Guid.NewGuid(), "meter", 1,
            Encoding.UTF8.GetBytes("{}"), "fixture", "fixture")], Token)).Committed);
        var request = new HttpCredentialRequest("http://10.0.0.1/", HttpCredentialKind.ApiKeyHeader, Name: "X-Key-Two", Secret: "replacement",
            ExpectedVersion: initial.Version, ExpectedDraftVersion: draft.Version);
        Assert.Equal("credential.accountDeclarationRequired", (await Assert.ThrowsAsync<CredentialWriteException>(() => store.WriteAsync(draft.Connection.Id, request, "admin", null, true, token: TestContext.Current.CancellationToken))).Code);
        var before = store.DescribeHttp(draft.Connection.Id)!.AccountMarker;
        await store.WriteAsync(draft.Connection.Id, request with { AccountChanged = true }, "admin", null, true, token: TestContext.Current.CancellationToken);
        Assert.NotEqual(before, store.DescribeHttp(draft.Connection.Id)!.AccountMarker);
        Assert.DoesNotContain(Secret, new MqttCredentialRequest("mqtt://10.0.0.1:1883", "user", Secret).ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ANewDraftCannotInheritAPreprovisionedSeedCredentialId(bool mqtt)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        using var admin = await Admin(factory);
        var time = new FakeTimeProvider(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));
        // Two identical generators only in this isolated test: predict a reserved seed ID without time races.
        var ids = new SnowflakeIdGenerator(0, time.GetUtcNow(), time, NullLogger.Instance);
        var predicted = new SnowflakeIdGenerator(0, time.GetUtcNow(), time, NullLogger.Instance).NextId();
        var db = factory.Services.GetRequiredService<ConfigDatabase>();
        if (mqtt)
        { db.Client.Insertable(new MqttCredentialRecord { ConnectionId = predicted }).ExecuteCommand(); }
        else
        { db.Client.Insertable(new HttpCredentialRecord { ConnectionId = predicted }).ExecuteCommand(); }
        var store = new ConnectionDraftStore(db, ids, factory.Services.GetRequiredService<AuditTrail>(), time,
            factory.Services.GetRequiredService<DeliveryCache>(), factory.Services.GetRequiredService<TenonEdge.Runtime.Connections.ConnectionModuleCatalog>());
        Assert.Equal("connection.reservedIdentity", Assert.Throws<DraftWriteException>(() => store.Write(null,
            new("0", "new", "New", ConnectionKind.Http, Http: new("http://10.0.0.1/")), "admin", null)).Code);
        Assert.Equal("0", store.Read().Version);
        Assert.Empty(store.Read().Connections);
    }

    private static ConnectionDraftResult CreateDraft(TenonEdgeApplicationFactory factory, bool mqtt) =>
        factory.Services.GetRequiredService<ConnectionDraftStore>().Write(null,
            new("0", "connection-1", "Connection", mqtt ? ConnectionKind.Mqtt : ConnectionKind.Http,
                Mqtt: mqtt ? new("10.0.0.1") : null, Http: mqtt ? null : new("http://10.0.0.1/")), "admin", null);
    private static Uri Path(long id, bool mqtt) => new($"/api/edge-admin/v1/connections/{id}/{(mqtt ? "mqtt" : "http")}-credential", UriKind.Relative);
    private static Task<HttpResponseMessage> Put(HttpClient client, ConnectionDraftResult draft, bool mqtt, string? expectedDraftVersion,
        string? recipient = null) => client.PutAsJsonAsync(Path(draft.Connection.Id, mqtt), mqtt
            ? (object)new { recipient = recipient ?? "mqtt://10.0.0.1:1883", username = "user", password = Secret, expectedDraftVersion }
            : new { recipient = recipient ?? "http://10.0.0.1/", kind = "bearer", secret = Secret, expectedDraftVersion }, Token);
    private static Task<HttpResponseMessage> Clear(HttpClient client, ConnectionDraftResult draft, bool mqtt, string version) =>
        client.DeleteAsync(new Uri(Path(draft.Connection.Id, mqtt).OriginalString + $"?expectedDraftVersion={draft.Version}&expectedVersion={version}", UriKind.Relative), Token);
    private static async Task<HttpClient> Admin(TenonEdgeApplicationFactory factory)
    {
        TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        return await factory.CreateLoggedInClientAsync("admin", Token);
    }
}
