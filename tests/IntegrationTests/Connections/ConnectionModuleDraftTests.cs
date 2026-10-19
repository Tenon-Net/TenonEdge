using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Parameters;
using TenonEdge.Host.Features.Configuration;
using TenonEdge.Host.Features.Configuration.Transfer;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Routes;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Security;
using TenonEdge.Host.Storage;
using TenonEdge.IntegrationTests.Infrastructure;

namespace TenonEdge.IntegrationTests.Connections;

public sealed class ConnectionModuleDraftTests
{
    private const string Endpoint = "/api/edge-admin/v1/connections";
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static JsonElement Json(string text) => JsonSerializer.Deserialize<JsonElement>(text);
    private static ConnectionModuleDefinition Definition(string kind = "testwire", int version = 1, bool requiredSecret = false) =>
        new(kind, version, "测试连接", new("connection.testwire", ParameterScope.Connection,
        [new("host", "主机", ParameterType.Text) { Required = true, MaximumLength = 255 },
         new("value", "精确值", ParameterType.Number) { WholeNumber = true, MinimumJson = "0", DefaultJson = "9007199254740993" },
         new("password", "凭证", ParameterType.Credential) { Required = requiredSecret }]));
    private static FixedDataDirectoryApplicationFactory Host(string path, bool installed = true) => new(path, configureServices: services =>
    {
        if (!installed)
        { return; }
        services.AddSingleton(Definition("testwire", 2));
        services.AddSingleton(Definition());
        services.AddSingleton(Definition("otherwire"));
        services.AddSingleton(Definition("secretwire", requiredSecret: true));
    });
    private static ConnectionDraftRequest Request(string version = "0") => new(version, "wire", "Wire", ConnectionKind.Module,
        Module: new("testwire", 1, Json("{\"host\":\"127.0.0.1\",\"value\":9007199254740993}")));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OutputModuleImportChecksActualAuxiliaryConnectionKindWithoutExecutingBinding(bool replace)
    {
        using var data = TemporaryDirectory.Create();
        var output = new TestProtocolOutputModule("http")
        {
            Parameters = new("output.testhttp", ParameterScope.Output,
            [new("connection", "HTTP", ParameterType.ConnectionReference) { Required = true, ConnectionKinds = ["http"] },
             new("wire", "Wire", ParameterType.ConnectionReference) { Required = true, ConnectionKinds = ["testwire"] },
             new("address", "Path", ParameterType.Text) { Required = true, MaximumLength = 1024 }]),
        };
        await using var host = new FixedDataDirectoryApplicationFactory(data.Path, configureServices: services =>
        {
            services.AddSingleton(Definition());
            services.AddSingleton(Definition("otherwire"));
            services.AddSingleton<IOutputModuleFactory>(output);
        });
        var file = new PortableConfiguration(1,
            [new("wire", "Wire", ConnectionKind.Module, true, null, null, null, null, new("testwire", 1, Request().Module!.Parameters)),
             new("sink", "Sink", ConnectionKind.Http, true, null, null, new("http://localhost/"), null)], [], [],
            [new("route", "Route", null, true, new(1,
                [new("out", "output.testhttp", 1, "Output", Json("{\"connectionKey\":\"sink\",\"parameters\":{\"address\":\"send\"},\"connectionKeys\":{\"connection\":\"sink\",\"wire\":\"wire\"}}"))], [], new([])))]);
        var store = host.Services.GetRequiredService<ConfigurationImportStore>();
        var mode = replace ? ConfigurationImportMode.Replace : ConfigurationImportMode.Merge;
        var candidate = store.Prepare(ConfigurationExport.Serialize(file, Token), mode, "0", Token);
        var wire = Assert.Single(candidate.Draft.Connections, value => value.Key == "wire");
        var graph = Assert.Single(candidate.Draft.Routes).Graph;
        Assert.Equal(wire.Id, Assert.Single(graph.Nodes).Config.GetProperty("connections").GetProperty("wire").GetInt64());
        Assert.Equal(0, output.Calls);
        file.Connections[0] = file.Connections[0] with { Module = file.Connections[0].Module! with { Kind = "otherwire" } };
        var failure = Assert.Throws<ConfigurationImportException>(() => store.Prepare(ConfigurationExport.Serialize(file, Token), mode, "0", Token));
        Assert.Contains(failure.Issues, issue => issue.Code == "import.protocolMismatch");
        Assert.Equal("0", host.Services.GetRequiredService<ConnectionDraftStore>().Read().Version);
        Assert.Equal(0, output.Calls);
    }

    [Theory]
    [InlineData("Viewer")]
    [InlineData("Configurator")]
    [InlineData("Administrator")]
    public async Task DiscoveryAndCrudKeepAuthenticationCsrfAndRoleBoundaries(string role)
    {
        using var data = TemporaryDirectory.Create();
        await using var host = Host(data.Path);
        using var anonymous = host.CreateClient();
        using var discoveryDenied = await anonymous.GetAsync("/api/edge-admin/v1/connection-modules", Token);
        using var writeDenied = await anonymous.PostJsonAsync(Endpoint, Request(), Token);
        Assert.Equal(HttpStatusCode.Unauthorized, discoveryDenied.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, writeDenied.StatusCode);
        TestAccounts.Create(host.Services, "operator", ApiRequests.StrongPassword, Enum.Parse<UserRole>(role));
        using var client = await host.CreateLoggedInClientAsync("operator", Token);
        using var discovery = await client.GetAsync("/api/edge-admin/v1/connection-modules", Token);
        Assert.Equal(HttpStatusCode.OK, discovery.StatusCode);
        Assert.True(discovery.Headers.CacheControl!.NoStore);
        var definitions = (await discovery.ReadJsonAsync(Token)).EnumerateArray().ToArray();
        Assert.Equal(["otherwire", "secretwire", "testwire", "testwire"], definitions.Select(value => value.GetProperty("kind").GetString()));
        Assert.Equal([1, 1, 1, 2], definitions.Select(value => value.GetProperty("version").GetInt32()));
        Assert.Equal("9007199254740993", definitions[2].GetProperty("parameters").GetProperty("parameters")[1].GetProperty("defaultJson").GetString());
        var store = host.Services.GetRequiredService<ConnectionDraftStore>();
        Assert.Empty(store.Read().Connections);
        var csrf = client.DefaultRequestHeaders.GetValues(ApiRequests.CsrfHeader).Single();
        client.DefaultRequestHeaders.Remove(ApiRequests.CsrfHeader);
        using var csrfDenied = await client.PostJsonAsync(Endpoint, Request(), Token);
        Assert.Equal(HttpStatusCode.Forbidden, csrfDenied.StatusCode);
        client.DefaultRequestHeaders.Add(ApiRequests.CsrfHeader, csrf);
        using var saved = await client.PostJsonAsync(Endpoint, Request(), Token);
        Assert.Equal(role == "Viewer" ? HttpStatusCode.Forbidden : HttpStatusCode.OK, saved.StatusCode);
        var created = role == "Viewer" ? store.Write(null, Request(), "fixture", null)
            : (await saved.Content.ReadFromJsonAsync<ConnectionDraftResult>(RouteSourceCompiler.Json, Token))!;
        Assert.Equal("9007199254740993", created.Connection.Module!.Parameters.GetProperty("value").GetRawText());
        using var read = await client.GetAsync(Endpoint, Token);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        using var update = await client.PutAsJsonAsync($"{Endpoint}/{created.Connection.Id}", Request(created.Version) with { Name = "Updated" }, Token);
        Assert.Equal(role == "Viewer" ? HttpStatusCode.Forbidden : HttpStatusCode.OK, update.StatusCode);
        using var delete = await client.DeleteAsync($"{Endpoint}/{created.Connection.Id}?expectedVersion={store.Read().Version}", Token);
        Assert.Equal(role == "Viewer" ? HttpStatusCode.Forbidden : HttpStatusCode.OK, delete.StatusCode);
        Assert.Null(host.Services.GetRequiredService<RuntimeVersionStore>().ReadPointers().EffectiveRevision);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("version")]
    [InlineData("range")]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("nested")]
    [InlineData("secret")]
    [InlineData("nullSecret")]
    [InlineData("requiredSecret")]
    [InlineData("missingParameters")]
    public async Task InvalidAdmissionDoesNotWriteOrEchoSubmittedValues(string reason)
    {
        using var data = TemporaryDirectory.Create();
        await using var host = Host(data.Path);
        TestAccounts.Create(host.Services, "operator", ApiRequests.StrongPassword, UserRole.Configurator);
        using var client = await host.CreateLoggedInClientAsync("operator", Token);
        var request = Request();
        request = request with
        {
            Module = reason switch
            {
                "missing" => request.Module! with { Kind = "missing" },
                "version" => request.Module! with { Version = 3 },
                "range" => request.Module! with { Parameters = Json("{\"host\":\"fixture-secret\",\"value\":-1}") },
                "unknown" => request.Module! with { Parameters = Json("{\"host\":\"fixture-secret\",\"unknown\":1}") },
                "duplicate" => request.Module! with { Parameters = Json("{\"host\":\"fixture-secret\",\"host\":\"other\"}") },
                "nested" => request.Module! with { Parameters = Json("{\"host\":{\"value\":\"fixture-secret\"}}") },
                "secret" => request.Module! with { Parameters = Json("{\"host\":\"local\",\"password\":\"fixture-secret\"}") },
                "nullSecret" => request.Module! with { Parameters = Json("{\"host\":\"local\",\"password\":null}") },
                "requiredSecret" => request.Module! with { Kind = "secretwire" },
                _ => request.Module! with { Parameters = Json("null") },
            }
        };
        using var response = await client.PostJsonAsync(Endpoint, request, Token);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain("fixture-secret", await response.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);
        Assert.Equal("0", host.Services.GetRequiredService<ConnectionDraftStore>().Read().Version);
        Assert.Empty(host.Services.GetRequiredService<ConnectionDraftStore>().Read().Connections);
        Assert.Empty(TestAccounts.Audit(host.Services, "connection.draftCreated"));
    }

    [Fact]
    public async Task ConcurrentWritesImmutableKindVersionSelectionAndAuditRollbackUseOriginalTransaction()
    {
        using var data = TemporaryDirectory.Create();
        await using var host = Host(data.Path);
        TestAccounts.Create(host.Services, "operator", ApiRequests.StrongPassword, UserRole.Configurator);
        using var client = await host.CreateLoggedInClientAsync("operator", Token);
        var responses = await Task.WhenAll(client.PostJsonAsync(Endpoint, Request(), Token),
            client.PostJsonAsync(Endpoint, Request() with { Key = "second" }, Token));
        foreach (var response in responses)
        { response.Dispose(); }
        Assert.Single(responses, value => value.StatusCode == HttpStatusCode.OK);
        Assert.Single(responses, value => value.StatusCode == HttpStatusCode.Conflict);
        var store = host.Services.GetRequiredService<ConnectionDraftStore>();
        var before = store.Read();
        var draft = Assert.Single(before.Connections);
        var request = Request(before.Version) with { Key = draft.Key };
        using var changedKind = await client.PutAsJsonAsync($"{Endpoint}/{draft.Id}", request with { Module = request.Module! with { Kind = "otherwire" } }, Token);
        Assert.Equal("connection.immutableIdentity", await changedKind.ReadProblemCodeAsync(Token));
        using var builtin = await client.PutAsJsonAsync($"{Endpoint}/{draft.Id}", request with { Kind = ConnectionKind.Http, Module = null, Http = new("http://localhost/") }, Token);
        Assert.Equal("connection.immutableIdentity", await builtin.ReadProblemCodeAsync(Token));
        var updated = store.Write(draft.Id, request with { Module = request.Module! with { Version = 2 } }, "operator", null);
        Assert.Equal(2, updated.Connection.Module!.Version);
        var db = host.Services.GetRequiredService<ConfigDatabase>();
        db.Client.Ado.ExecuteCommand("CREATE TRIGGER fail_connection_module_audit BEFORE INSERT ON audit_log WHEN NEW.action = 'connection.draftUpdated' BEGIN SELECT RAISE(ABORT, 'fixture'); END;");
        using var failed = await client.PutAsJsonAsync($"{Endpoint}/{draft.Id}", request with { ExpectedVersion = updated.Version, Name = "Must roll back" }, Token);
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.Equal(updated.Version, store.Read().Version);
        Assert.Equal(updated.Connection.Name, Assert.Single(store.Read().Connections).Name);
    }

    [Fact]
    public async Task RestartWithoutDeclarationStillReadsExportsAndDeletesButRejectsNewAdmission()
    {
        using var data = TemporaryDirectory.Create();
        byte[] exported;
        ConnectionDraftResult saved;
        await using (var host = Host(data.Path))
        {
            saved = host.Services.GetRequiredService<ConnectionDraftStore>().Write(null, Request(), "fixture", null);
            exported = host.Services.GetRequiredService<ConfigurationExportStore>().Export(null, Token);
            var compiled = RoutePlanCompiler.Compile(host.Services.GetRequiredService<RouteDraftStore>().ReadCompilation(saved.Version, Token), Token);
            Assert.Empty(compiled.Document.Units); // A new input connection is not an HTTP queue sender.
        }
        await using var missing = Host(data.Path, installed: false);
        var store = missing.Services.GetRequiredService<ConnectionDraftStore>();
        var draft = Assert.Single(store.Read().Connections);
        Assert.Equal(saved.Connection.Id, draft.Id);
        Assert.Equal("9007199254740993", draft.Module!.Parameters.GetProperty("value").GetRawText());
        Assert.Equal(exported, missing.Services.GetRequiredService<ConfigurationExportStore>().Export(null, Token));
        Assert.Throws<ArgumentException>(() => store.Write(draft.Id, Request(saved.Version), "fixture", null));
        Assert.Throws<RouteTopologyException>(() => RoutePlanCompiler.Compile(missing.Services.GetRequiredService<RouteDraftStore>().ReadCompilation(saved.Version, Token), Token));
        TestAccounts.Create(missing.Services, "viewer", ApiRequests.StrongPassword, UserRole.Viewer);
        using var viewer = await missing.CreateLoggedInClientAsync("viewer", Token);
        var runtime = await viewer.GetJsonAsync("/api/edge-admin/v1/runtime/connections", Token);
        var row = Assert.Single(runtime.GetProperty("connections").EnumerateArray());
        Assert.Equal("testwire", row.GetProperty("draft").GetProperty("kind").GetString());
        Assert.DoesNotContain("127.0.0.1", row.GetRawText(), StringComparison.Ordinal);
        using var discovery = await viewer.GetAsync("/api/edge-admin/v1/connection-modules", Token);
        Assert.Empty((await discovery.ReadJsonAsync(Token)).EnumerateArray());
        _ = store.Delete(draft.Id, saved.Version, "fixture", null);
        Assert.Empty(store.Read().Connections);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PortableImportUsesLocalKeysRejectsChangedKindsAndRetainsExactParameters(bool replace)
    {
        using var sourceData = TemporaryDirectory.Create();
        using var targetData = TemporaryDirectory.Create();
        byte[] file;
        long sourceId;
        await using (var source = Host(sourceData.Path))
        {
            sourceId = source.Services.GetRequiredService<ConnectionDraftStore>().Write(null, Request(), "fixture", null).Connection.Id;
            file = source.Services.GetRequiredService<ConfigurationExportStore>().Export(null, Token);
        }
        Assert.DoesNotContain(sourceId.ToString(System.Globalization.CultureInfo.InvariantCulture), Encoding.UTF8.GetString(file), StringComparison.Ordinal);
        await using var target = Host(targetData.Path);
        var mode = replace ? ConfigurationImportMode.Replace : ConfigurationImportMode.Merge;
        var store = target.Services.GetRequiredService<ConfigurationImportStore>();
        var candidate = store.Prepare(file, mode, "0", Token);
        var draft = Assert.Single(candidate.Draft.Connections);
        Assert.NotEqual(sourceId, draft.Id);
        Assert.Equal("9007199254740993", draft.Module!.Parameters.GetProperty("value").GetRawText());
        TestAccounts.Create(target.Services, "operator", ApiRequests.StrongPassword, UserRole.Configurator);
        var actor = TestAccounts.Find(target.Services, "operator")!;
        var saved = store.Commit(candidate, new(actor.Id, actor.SecurityStamp, null), Token);
        var same = store.Prepare(file, mode, saved.Version, Token);
        Assert.Equal(draft.Id, Assert.Single(same.Draft.Connections).Id);
        Assert.Empty(same.Changes);
        var portable = ConfigurationImportDocument.Read(file, Token);
        foreach (var changed in new[]
        {
            portable.Connections[0] with { Module = portable.Connections[0].Module! with { Kind = "otherwire" } },
            portable.Connections[0] with { Kind = ConnectionKind.Http, Module = null, Http = new("http://localhost/") },
            portable.Connections[0] with { Module = portable.Connections[0].Module! with { Version = 3 } },
            portable.Connections[0] with { Credential = new("requiresInput", "sha256:" + new string('a', 64)) },
        })
        {
            var bad = portable with { Connections = [changed] };
            Assert.Throws<ConfigurationImportException>(() => store.Prepare(ConfigurationExport.Serialize(bad, Token), mode, saved.Version, Token));
            Assert.Equal(saved.Version, target.Services.GetRequiredService<ConnectionDraftStore>().Read().Version);
        }
        Assert.Null(target.Services.GetRequiredService<RuntimeVersionStore>().ReadPointers().EffectiveRevision);
    }
}
