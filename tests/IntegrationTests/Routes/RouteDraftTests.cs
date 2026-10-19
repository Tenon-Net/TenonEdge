using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Host.Features.Configuration;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Devices;
using TenonEdge.Host.Features.Inputs;
using TenonEdge.Host.Features.Routes;
using TenonEdge.Host.Security;
using TenonEdge.Host.Storage;
using TenonEdge.Host.Storage.Migrations;
using TenonEdge.IntegrationTests.Infrastructure;

namespace TenonEdge.IntegrationTests.Routes;

public sealed class RouteDraftTests
{
    private const string Endpoint = "/api/edge-admin/v1/routes/";
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static RouteGraph Graph(long? input = null, long? connection = null) => new(1,
        [new("in", "input.simulator", 1, "source", JsonSerializer.SerializeToElement(new { sourceIds = input is { } id ? new[] { id } : [] })),
         new("out", "output.http", 1, "target", JsonSerializer.SerializeToElement(new { connectionId = connection }))],
        [new("in", "out")], new(new() { ["in"] = new(10, 20), ["out"] = new(310, 20) }));
    private static RouteDraftRequest Request(string version = "0") => new(version, "route", "Unfinished route", true, Graph());

    [Theory]
    [InlineData("Viewer", HttpStatusCode.Forbidden)]
    [InlineData("Configurator", HttpStatusCode.OK)]
    [InlineData("Administrator", HttpStatusCode.OK)]
    public async Task RolesCanReadButOnlyEditorsCanCreateUpdateCopyAndDelete(string role, HttpStatusCode expected)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "operator", ApiRequests.StrongPassword, Enum.Parse<UserRole>(role));
        using var client = await factory.CreateLoggedInClientAsync("operator", Token);
        var store = factory.Services.GetRequiredService<RouteDraftStore>();
        using var read = await client.GetAsync(new Uri(Endpoint, UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        using var create = await client.PostJsonAsync(Endpoint, Request(), Token);
        Assert.Equal(expected, create.StatusCode);
        var item = expected == HttpStatusCode.OK ? store.Read(Assert.Single(store.Read().Routes).Id) : store.Write(null, Request(), "fixture", null);
        using var detail = await client.GetAsync(new Uri(Endpoint + item.Route.Id, UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        using var update = await client.PutAsJsonAsync(new Uri(Endpoint + item.Route.Id, UriKind.Relative), Request(item.Version) with { Enabled = false }, Token);
        Assert.Equal(expected, update.StatusCode);
        using var copy = await client.PostJsonAsync(Endpoint + item.Route.Id + "/copy", new RouteDraftCopyRequest(store.Read().Version, "copy", "Copy"), Token);
        Assert.Equal(expected, copy.StatusCode);
        using var delete = await client.DeleteAsync(new Uri($"{Endpoint}{item.Route.Id}?expectedVersion={store.Read().Version}", UriKind.Relative), Token);
        Assert.Equal(expected, delete.StatusCode);
        Assert.Single(store.Read().Routes);
        if (expected == HttpStatusCode.OK)
        { Assert.False(store.Read().Routes[0].Enabled); }
    }

    [Fact]
    public async Task AnonymousCsrfAndUnknownSecretPropertiesCannotModifyDrafts()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        using var anonymous = factory.CreateClient();
        using var read = await anonymous.GetAsync(new Uri(Endpoint, UriKind.Relative), Token);
        using var write = await anonymous.PostJsonAsync(Endpoint, Request(), Token);
        Assert.Equal(HttpStatusCode.Unauthorized, read.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, write.StatusCode);
        TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        using var admin = await factory.CreateLoggedInClientAsync("admin", Token);
        var graph = Graph();
        graph.Nodes[1] = graph.Nodes[1] with { Config = JsonSerializer.Deserialize<JsonElement>("{\"password\":\"not-persisted\"}") };
        using var secret = await admin.PostJsonAsync(Endpoint, Request() with { Graph = graph }, Token);
        Assert.Equal(HttpStatusCode.BadRequest, secret.StatusCode);
        admin.DefaultRequestHeaders.Remove(ApiRequests.CsrfHeader);
        using var csrf = await admin.PostJsonAsync(Endpoint, Request(), Token);
        Assert.Equal(HttpStatusCode.Forbidden, csrf.StatusCode);
        var store = factory.Services.GetRequiredService<RouteDraftStore>();
        Assert.Empty(store.Read().Routes);
        Assert.Equal("0", store.Read().Version);
    }

    [Fact]
    public async Task RouteListReportsDescriptionEnablementUpdateAndCompilationEffectiveState()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var store = factory.Services.GetRequiredService<RouteDraftStore>();
        var created = store.Write(null, Request() with { Description = "north line" }, "editor", null);
        var listed = store.Read();
        var route = Assert.Single(listed.Routes);
        Assert.Equal("north line", route.Description);
        Assert.True(route.Enabled);
        Assert.Equal("editor", route.UpdatedBy);
        Assert.True(route.UpdatedAt > 0);
        Assert.False(route.Effective);
        Assert.True(route.HasErrors);
        Assert.True(route.HasUnappliedChanges);

    }

    [Fact]
    public async Task OversizedBodyIsRejectedBeforeDraftMutation()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "真实进程验证依赖 Linux 的 SIGTERM 与 flock。");
        using var working = TemporaryDirectory.Create();
        using var data = TemporaryDirectory.Create();
        await using var host = HostProcess.Start(working.Path, "Production", data.Path);
        var timeout = TimeSpan.FromSeconds(30);
        var address = await host.WaitForListeningAddressAsync(timeout, Token);
        using var admin = new HttpClient { BaseAddress = address, Timeout = timeout };
        await host.WaitUntilReadyAsync(admin, timeout, Token);
        using var setup = await admin.CompleteSetupAsync(Token);
        Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
        await admin.UseSessionAsync(setup, Token);
        using var body = new StringContent(new string(' ', 1024 * 1024) + "{}", System.Text.Encoding.UTF8, "application/json");
        using var response = await admin.PostAsync(new Uri(Endpoint, UriKind.Relative), body, Token);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        var list = await admin.GetJsonAsync(Endpoint, Token);
        Assert.Empty(list.GetProperty("routes").EnumerateArray());
        await host.RequestStopAsync(Token);
        Assert.Equal(0, await host.WaitForExitAsync(timeout, Token));
    }

    [Fact]
    public async Task IncompleteGraphsAndLayoutSurviveRestartWithoutTouchingRuntimePointers()
    {
        using var data = TemporaryDirectory.Create();
        long id;
        string version;
        await using (var factory = new FixedDataDirectoryApplicationFactory(data.Path))
        {
            var store = factory.Services.GetRequiredService<RouteDraftStore>();
            var graph = Graph();
            graph = graph with
            {
                Nodes = [.. graph.Nodes, new("map", "transform.map", 1, "Exact constant",
                JsonSerializer.Deserialize<JsonElement>("""{"rules":[{"target":"exact","type":"integer","constant":9223372036854775807}]}"""))]
            };
            var result = store.Write(null, Request() with { Graph = graph }, "editor", null);
            id = result.Route.Id;
            version = result.Version;
            var state = factory.Services.GetRequiredService<ConfigDatabase>().Client.Queryable<SystemStateRecord>().InSingle(1);
            Assert.Null(state.EffectiveRevision);
            Assert.Null(state.PreviousRevision);
            Assert.Null(state.PendingRevision);
            Assert.Equal("editor", result.Route.UpdatedBy);
            Assert.True(result.Route.UpdatedAt > 0);
        }
        await using var restarted = new FixedDataDirectoryApplicationFactory(data.Path);
        var saved = restarted.Services.GetRequiredService<RouteDraftStore>().Read(id);
        Assert.Equal(version, saved.Version);
        Assert.Equal(new RouteNodePosition(310, 20), saved.Route.Graph.Layout.Nodes["out"]);
        Assert.Empty(saved.Route.Graph.Nodes[0].Config.GetProperty("sourceIds").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, saved.Route.Graph.Nodes[1].Config.GetProperty("connectionId").ValueKind);
        Assert.Equal("9223372036854775807", saved.Route.Graph.Nodes[2].Config.GetProperty("rules")[0].GetProperty("constant").GetRawText());
    }

    [Fact]
    public async Task ConcurrentSavesImmutableKeysAndStaleDeletesPreserveTheGlobalDraft()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        using var client = await factory.CreateLoggedInClientAsync("admin", Token);
        var responses = await Task.WhenAll(client.PostJsonAsync(Endpoint, Request(), Token), client.PostJsonAsync(Endpoint, Request() with { Key = "other" }, Token));
        using var first = responses[0];
        using var second = responses[1];
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);
        var store = factory.Services.GetRequiredService<RouteDraftStore>();
        var snapshot = store.Read();
        var item = Assert.Single(snapshot.Routes);
        using var duplicate = await client.PostJsonAsync(Endpoint, Request(snapshot.Version) with { Key = item.Key }, Token);
        Assert.Equal("route.duplicateKey", await duplicate.ReadProblemCodeAsync(Token));
        using var immutable = await client.PutAsJsonAsync(new Uri(Endpoint + item.Id, UriKind.Relative), Request(snapshot.Version) with { Key = "replacement" }, Token);
        Assert.Equal("route.immutableKey", await immutable.ReadProblemCodeAsync(Token));
        using var stale = await client.DeleteAsync(new Uri($"{Endpoint}{item.Id}?expectedVersion=0", UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        using var missing = await client.GetAsync(new Uri(Endpoint + "1", UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(snapshot.Version, store.Read().Version);
    }

    [Fact]
    public async Task EvenDisabledCopiesProtectSourceDeviceAndConnectionUntilReferencesAreRemoved()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var devices = factory.Services.GetRequiredService<DeviceDraftStore>();
        var inputs = factory.Services.GetRequiredService<InputDraftStore>();
        var connections = factory.Services.GetRequiredService<ConnectionDraftStore>();
        var routes = factory.Services.GetRequiredService<RouteDraftStore>();
        var device = devices.Write(null, new("0", "meter", "Meter", DeviceKind.Simulator), "fixture", null);
        var input = inputs.Write(device.Device.Id, null, new(device.Version, "sample", "Sample", Simulator: new()), "fixture", null);
        var connection = connections.Write(null, new(input.Version, "http", "HTTP", ConnectionKind.Http, Http: new("http://localhost/")), "fixture", null);
        var route = routes.Write(null, Request(connection.Version) with { Graph = Graph(input.Input.Id, connection.Connection.Id) }, "fixture", null);
        var copy = routes.Copy(route.Route.Id, new(route.Version, "copy", "Copy"), "fixture", null);
        Assert.False(copy.Route.Enabled);
        Assert.Equal(JsonSerializer.Serialize(route.Route.Graph), JsonSerializer.Serialize(copy.Route.Graph));
        var version = routes.Delete(route.Route.Id, copy.Version, "fixture", null).Version;
        Assert.Equal("input.routeReferenced", Assert.Throws<DraftWriteException>(() => inputs.Delete(device.Device.Id, input.Input.Id, version, "fixture", null)).Code);
        Assert.Equal("device.routeReferenced", Assert.Throws<DraftWriteException>(() => devices.Delete(device.Device.Id, version, "fixture", null)).Code);
        Assert.Equal("connection.routeReferenced", Assert.Throws<DraftWriteException>(() => connections.Delete(connection.Connection.Id, version, "fixture", null)).Code);
        var empty = routes.Write(copy.Route.Id, Request(version) with { Key = "copy", Graph = Graph(), Enabled = false }, "fixture", null);
        version = inputs.Delete(device.Device.Id, input.Input.Id, empty.Version, "fixture", null).Version;
        version = devices.Delete(device.Device.Id, version, "fixture", null).Version;
        connections.Delete(connection.Connection.Id, version, "fixture", null);
        Assert.Empty(factory.Services.GetRequiredService<ConfigDatabase>().Client.Queryable<RouteDraftReferenceRecord>().ToList());
    }

    [Fact]
    public void UpgradeFromSchemaFourteenPreservesTheExistingDraftAndAddsEmptyRouteTables()
    {
        using var store = TestConfigStore.Create();
        store.Initialize(ConfigMigrations.All.Where(migration => migration.Version <= 14).ToArray());
        store.Database.Client.Ado.ExecuteCommand("UPDATE configuration_draft SET version = 'existing-draft' WHERE id = 1");
        store.Database.Client.Ado.ExecuteCommand("INSERT INTO device_draft(id,business_key,content) VALUES (123,'kept','{}')");
        Assert.Equal(ConfigMigrations.All[^1].Version, store.CreateMigrator().Migrate());
        Assert.Equal("existing-draft", store.Database.Client.Ado.GetString("SELECT version FROM configuration_draft WHERE id = 1"));
        Assert.Equal("kept", store.Database.Client.Ado.GetString("SELECT business_key FROM device_draft WHERE id = 123"));
        Assert.Equal(0, store.Database.Client.Queryable<RouteDraftRecord>().Count());
        Assert.Equal(0, store.Database.Client.Queryable<RouteDraftReferenceRecord>().Count());
        Assert.Equal(ConfigMigrations.All[^1].Version, store.CreateMigrator().Migrate());
    }

    [Fact]
    public async Task AuditFailureRollsBackGraphReferencesAndVersionTogether()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var store = factory.Services.GetRequiredService<RouteDraftStore>();
        var db = factory.Services.GetRequiredService<ConfigDatabase>();
        var saved = store.Write(null, Request() with { Graph = Graph(7, 8) }, "fixture", null);
        db.Client.Ado.ExecuteCommand("CREATE TRIGGER fail_route_audit BEFORE INSERT ON audit_log WHEN NEW.action = 'route.draftUpdated' BEGIN SELECT RAISE(ABORT, 'audit failure'); END;");
        Assert.ThrowsAny<Exception>(() => store.Write(saved.Route.Id, Request(saved.Version), "fixture", null));
        Assert.Equal(saved.Version, store.Read().Version);
        Assert.Equal(2, db.Client.Queryable<RouteDraftReferenceRecord>().Count());
        Assert.Equal(7, store.Read(saved.Route.Id).Route.Graph.Nodes[0].Config.GetProperty("sourceIds")[0].GetInt64());
        Assert.Empty(TestAccounts.Audit(factory.Services, "route.draftUpdated"));
    }
}
