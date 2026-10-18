using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Host.Features.Configuration;
using TenonEdge.Host.Features.Configuration.Transfer;
using TenonEdge.Host.Features.Routes;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Security;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.IntegrationTests.Runtime;
using TenonEdge.Runtime.Deliveries;

namespace TenonEdge.IntegrationTests.Routes;

public sealed class RouteTransformTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static JsonElement Json(string text) => JsonSerializer.Deserialize<JsonElement>(text);
    internal static RouteGraph Graph(RouteGraph original, string config = "{\"offset\":1}") => original with
    {
        Nodes = [original.Nodes[0], new("custom", "transform.test-offset", 1, "Offset", Json(config)),
            new("map", "transform.map", 1, "Map", Json("""{"rules":[{"target":"final","type":"integer","sourceField":"value"}]}""")), original.Nodes[1]],
        Edges = [new("in", "custom"), new("custom", "map"), new("map", "out")],
    };
    private static FixedDataDirectoryApplicationFactory Host(string path) => new(path, configureServices:
        services => services.AddSingleton<IMessageTransformFactory>(new TestOffsetTransformFactory()));

    [Theory]
    [InlineData("Viewer")]
    [InlineData("Configurator")]
    [InlineData("Administrator")]
    public async Task DiscoveryIsAuthenticatedNoStoreAndUsesActualRegisteredModule(string role)
    {
        using var directory = TemporaryDirectory.Create();
        await using var host = Host(directory.Path);
        using var anonymous = host.CreateClient();
        using var denied = await anonymous.GetAsync(new Uri("/api/edge-admin/v1/transform-modules", UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        TestAccounts.Create(host.Services, "operator", ApiRequests.StrongPassword, Enum.Parse<UserRole>(role));
        using var client = await host.CreateLoggedInClientAsync("operator", Token);
        using var response = await client.GetAsync(new Uri("/api/edge-admin/v1/transform-modules", UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        var modules = (await response.ReadJsonAsync(Token)).EnumerateArray().ToArray();
        Assert.Equal(2, modules.Length);
        var module = modules.Single(value => value.GetProperty("kind").GetString() == "transform.test-offset");
        Assert.True(module.GetProperty("canConfigure").GetBoolean());
        Assert.Equal(1, module.GetProperty("version").GetInt32());
        Assert.Equal("offset", module.GetProperty("parameters").GetProperty("parameters")[0].GetProperty("name").GetString());
        Assert.Empty(host.Services.GetRequiredService<RouteDraftStore>().Read().Routes);
    }

    [Fact]
    public async Task RealDraftApiCompilerFieldsAndPreviewUseInstalledModuleWhileIncompleteDraftCannotApply()
    {
        using var directory = TemporaryDirectory.Create();
        await using var host = Host(directory.Path);
        TestAccounts.Create(host.Services, "operator", ApiRequests.StrongPassword, UserRole.Configurator);
        using var client = await host.CreateLoggedInClientAsync("operator", Token);
        var seeded = RoutePreviewTests.Seed(host.Services, 18191);
        var request = new RouteDraftRequest(seeded.ExpectedVersion, "extended", "Extended", true, Graph(seeded.Graph, "{}"));
        using var save = await client.PostJsonAsync("/api/edge-admin/v1/routes", request, Token);
        Assert.Equal(HttpStatusCode.OK, save.StatusCode);
        var store = host.Services.GetRequiredService<RouteDraftStore>();
        var saved = store.Read(Assert.Single(store.Read().Routes).Id);
        using var invalid = await client.PostJsonAsync("/api/edge-admin/v1/routes/compile", new RouteCompileRequest(saved.Version), Token);
        Assert.False((await invalid.ReadJsonAsync(Token)).GetProperty("compiled").GetBoolean());
        Assert.False(host.Services.GetRequiredService<RuntimeVersionStore>().ReviewDraft(saved.Version, Token).PreflightPassed);
        var good = store.Write(saved.Route.Id, request with { ExpectedVersion = saved.Version, Graph = Graph(seeded.Graph) }, "operator", null);
        var compiled = RoutePlanCompiler.Compile(store.ReadCompilation(good.Version, Token), Token);
        var output = Assert.Single(compiled.Document.Units, unit => unit.Id.Kind == "output.http");
        Assert.False(output.Configuration.TryGetProperty("mappingStages", out _));
        Assert.Equal(2, output.Configuration.GetProperty("transformStages").GetArrayLength());
        Assert.Equal("final", Assert.Single(Assert.Single(compiled.Paths).Fields).Key);
        Assert.Equal(compiled.Document.ContentHash, host.Services.GetRequiredService<RuntimeVersionStore>().ReviewDraft(good.Version, Token).PlanHash);
        using var fields = await client.PostJsonAsync("/api/edge-admin/v1/routes/fields", new RouteFieldRequest(good.Version, good.Route.Graph, "map"), Token);
        Assert.Equal(HttpStatusCode.OK, fields.StatusCode);
        Assert.Contains("value", await fields.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);
        using var preview = await client.PostJsonAsync("/api/edge-admin/v1/routes/preview",
            seeded with { ExpectedVersion = good.Version, Graph = good.Route.Graph, SampleJson = "{\"value\":9007199254740993}" }, Token);
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        var result = await preview.ReadJsonAsync(Token);
        Assert.True(result.GetProperty("previewed").GetBoolean());
        var body = result.GetProperty("result").GetProperty("paths")[0].GetProperty("output").GetProperty("body").GetString();
        Assert.Contains("\"final\":9007199254740994", body, StringComparison.Ordinal);
        Assert.Equal(0, host.Services.GetRequiredService<DeliveryCache>().GetStatus().CommittedRecords);
        var copy = store.Copy(good.Route.Id, new(good.Version, "copy", "Copy"), "operator", null);
        Assert.False(copy.Route.Enabled);
        Assert.Equal(good.Route.Graph.Nodes[1].Config.GetRawText(), copy.Route.Graph.Nodes[1].Config.GetRawText());
        Assert.Equal("configuration.draftConflict", Assert.Throws<DraftWriteException>(() => store.Copy(good.Route.Id,
            new(good.Version, "stale", "Stale"), "operator", null)).Code);
    }

    [Theory]
    [InlineData("{\"offset\":1,\"secret\":\"hidden\"}", 1)]
    [InlineData("{\"offset\":{}}", 1)]
    [InlineData("{\"offset\":1,\"offset\":2}", 1)]
    [InlineData("{\"offset\":1}", 2)]
    public async Task InvalidStructureOrMissingVersionCannotMutateDraft(string config, int version)
    {
        using var directory = TemporaryDirectory.Create();
        await using var host = Host(directory.Path);
        var seeded = RoutePreviewTests.Seed(host.Services, 18191);
        var graph = Graph(seeded.Graph, config);
        graph.Nodes[1] = graph.Nodes[1] with { Version = version };
        var store = host.Services.GetRequiredService<RouteDraftStore>();
        Assert.Throws<ArgumentException>(() => store.Write(null, new(seeded.ExpectedVersion, "extended", "Extended", true, graph), "test", null));
        Assert.Equal(seeded.ExpectedVersion, store.Read().Version);
        Assert.Empty(store.Read().Routes);
    }

    [Fact]
    public async Task CrossHostExportImportAndRestartPreserveCustomGraphAndRejectMissingModules()
    {
        using var sourceData = TemporaryDirectory.Create();
        byte[] file;
        string exportedConfig;
        await using (var source = Host(sourceData.Path))
        {
            var seeded = RoutePreviewTests.Seed(source.Services, 18191);
            var saved = source.Services.GetRequiredService<RouteDraftStore>().Write(null,
                new(seeded.ExpectedVersion, "extended", "Extended", true, Graph(seeded.Graph, "{\"offset\":1.000000000000000000000}")), "test", null);
            exportedConfig = saved.Route.Graph.Nodes[1].Config.GetRawText();
            file = source.Services.GetRequiredService<ConfigurationExportStore>().Export(null, Token);
        }
        using var destination = TemporaryDirectory.Create();
        string version;
        await using (var target = Host(destination.Path))
        {
            TestAccounts.Create(target.Services, "importer", ApiRequests.StrongPassword, UserRole.Configurator);
            var account = TestAccounts.Find(target.Services, "importer")!;
            var store = target.Services.GetRequiredService<ConfigurationImportStore>();
            var candidate = store.Prepare(file, ConfigurationImportMode.Replace, "0", Token);
            version = store.Commit(candidate, new(account.Id, account.SecurityStamp, "127.0.0.1"), Token).Version;
            var drafts = target.Services.GetRequiredService<RouteDraftStore>();
            var graph = drafts.Read(Assert.Single(drafts.Read().Routes).Id).Route.Graph;
            Assert.Equal(exportedConfig, graph.Nodes[1].Config.GetRawText());
            Assert.Equal(version, drafts.Read().Version);
        }
        // A deployment without the extension can read/export the graph but cannot re-import or compile it.
        await using var missing = new FixedDataDirectoryApplicationFactory(destination.Path);
        var routes = missing.Services.GetRequiredService<RouteDraftStore>();
        Assert.Equal(exportedConfig, routes.Read(Assert.Single(routes.Read().Routes).Id).Route.Graph.Nodes[1].Config.GetRawText());
        Assert.NotEmpty(missing.Services.GetRequiredService<ConfigurationExportStore>().Export(null, Token));
        Assert.Throws<RouteTopologyException>(() => RoutePlanCompiler.Compile(routes.ReadCompilation(version, Token), Token));
        Assert.Throws<ConfigurationImportException>(() => missing.Services.GetRequiredService<ConfigurationImportStore>().Prepare(file, ConfigurationImportMode.Replace, version, Token));
        Assert.Equal(version, routes.Read().Version);
    }
}
