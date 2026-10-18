using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Parameters;
using TenonEdge.Host.Features.Configuration;
using TenonEdge.Host.Features.Configuration.Transfer;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Devices;
using TenonEdge.Host.Features.Inputs;
using TenonEdge.Host.Features.Routes;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Security;
using TenonEdge.Host.Storage;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.Runtime.Deliveries;
using TenonEdge.Runtime.Outputs;

namespace TenonEdge.IntegrationTests.Routes;

public sealed partial class RouteOutputModuleTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    internal static JsonElement Json(string text) => JsonSerializer.Deserialize<JsonElement>(text);
    private static FixedDataDirectoryApplicationFactory Host(string path, IOutputModuleFactory module) => new(path,
        configureServices: services => services.AddSingleton(module));

    internal static RoutePreviewRequest Seed(IServiceProvider services, string protocol, int port, bool map = true)
    {
        var connections = services.GetRequiredService<ConnectionDraftStore>();
        var target = connections.Write(null, new(connections.Read().Version, "sink", "Sink",
            protocol == "http" ? ConnectionKind.Http : ConnectionKind.Mqtt,
            Http: protocol == "http" ? new($"http://127.0.0.1:{port}/") : null,
            Mqtt: protocol == "mqtt" ? new("127.0.0.1", port) : null), "fixture", null);
        var device = services.GetRequiredService<DeviceDraftStore>().Write(null,
            new(target.Version, "device", "Device", DeviceKind.Simulator), "fixture", null);
        var input = services.GetRequiredService<InputDraftStore>().Write(device.Device.Id, null,
            new(device.Version, "source", "Source", Simulator: new(PeriodMs: 3600000, SampleJson: "{\"value\":9007199254740993}")), "fixture", null);
        var source = new RouteGraphNode("in", "input.simulator", 1, "Source", JsonSerializer.SerializeToElement(new { sourceIds = new[] { input.Input.Id } }));
        var output = new RouteGraphNode("out", "output.test" + protocol, 1, "Module", JsonSerializer.SerializeToElement(new
        {
            connectionId = target.Connection.Id,
            parameters = new { address = protocol == "http" ? "events" : "events/{deviceKey}" },
            connections = new { connection = target.Connection.Id },
        }));
        var middle = new RouteGraphNode("map", "transform.map", 1, "Map", Json("""{"rules":[{"sourceField":"value","target":"scaled","type":"integer","offset":1}]}"""));
        return new(input.Version, new(1, map ? [source, middle, output] : [source, output],
            map ? [new("in", "map"), new("map", "out")] : [new("in", "out")], new([])), input.Input.Id, "{\"value\":9007199254740993}");
    }

    internal static async Task<(JsonElement Output, object Confirmation)> PreviewAndPrepare(HttpClient client, RoutePreviewRequest request)
    {
        using var preview = await client.PostJsonAsync("/api/edge-admin/v1/routes/preview", request, Token);
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        var output = Assert.Single((await preview.ReadJsonAsync(Token)).GetProperty("result").GetProperty("paths").EnumerateArray()).GetProperty("output");
        var id = output.GetProperty("testSendId").GetGuid();
        using var prepare = await client.PostJsonAsync("/api/edge-admin/v1/routes/test-send/prepare", new { id }, Token);
        Assert.Equal(HttpStatusCode.OK, prepare.StatusCode);
        var review = await prepare.ReadJsonAsync(Token);
        Assert.Equal(output.GetProperty("kind").GetString(), review.GetProperty("kind").GetString());
        Assert.Equal(output.GetProperty("deliveryId").GetGuid(), review.GetProperty("deliveryId").GetGuid());
        Assert.Equal(output.GetProperty("bodyBytes").GetInt32(), review.GetProperty("bodyBytes").GetInt32());
        return (output, new { id, confirmationId = review.GetProperty("confirmationId").GetGuid(), confirmed = true });
    }

    [Theory]
    [InlineData("http")]
    [InlineData("mqtt")]
    public async Task DraftCompilerFieldsAndPurePreviewUseOriginalProtocolWithoutDoubleMapping(string protocol)
    {
        using var data = TemporaryDirectory.Create();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var module = new TestProtocolOutputModule(protocol);
        await using var host = Host(data.Path, module);
        var seeded = Seed(host.Services, protocol, ((IPEndPoint)listener.LocalEndpoint).Port);
        TestAccounts.Create(host.Services, "editor", ApiRequests.StrongPassword, UserRole.Configurator);
        using var client = await host.CreateLoggedInClientAsync("editor", Token);
        using var saved = await client.PostJsonAsync("/api/edge-admin/v1/routes", new RouteDraftRequest(seeded.ExpectedVersion, "module_route", "Module", true, seeded.Graph), Token);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal(0, module.Calls); // Saving only uses declaration validation, not module code.
        var drafts = host.Services.GetRequiredService<RouteDraftStore>();
        var version = drafts.Read().Version;
        var compiled = RoutePlanCompiler.Compile(drafts.ReadCompilation(version, Token), Token);
        var output = Assert.Single(compiled.Document.Units, unit => unit.Id.Kind.StartsWith("output.", StringComparison.Ordinal));
        Assert.Equal(module.Kind, output.Id.Kind);
        Assert.Equal("output." + protocol, OutputModuleCatalog.ReadProtocol(output)!.Id.Kind);
        Assert.True(output.Configuration.TryGetProperty("mappingStages", out _));
        Assert.False(OutputModuleCatalog.ReadProtocol(output)!.Configuration.TryGetProperty("mappingStages", out _));
        Assert.Equal("scaled", Assert.Single(Assert.Single(compiled.Paths).Fields).Key);
        Assert.True(host.Services.GetRequiredService<RuntimeVersionStore>().ReviewDraft(version, Token).PreflightPassed);
        using var fields = await client.PostJsonAsync("/api/edge-admin/v1/routes/fields", new RouteFieldRequest(version, seeded.Graph, "out"), Token);
        Assert.Equal(HttpStatusCode.OK, fields.StatusCode);
        Assert.Contains("scaled", await fields.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);
        using var preview = await client.PostJsonAsync("/api/edge-admin/v1/routes/preview", seeded with { ExpectedVersion = version }, Token);
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        var result = await preview.ReadJsonAsync(Token);
        Assert.True(result.GetProperty("previewed").GetBoolean());
        var path = Assert.Single(result.GetProperty("result").GetProperty("paths").EnumerateArray());
        Assert.True(path.GetProperty("succeeded").GetBoolean());
        Assert.Equal("output." + protocol, path.GetProperty("output").GetProperty("kind").GetString());
        Assert.Contains("\"scaled\":9007199254740994", path.GetProperty("output").GetProperty("body").GetString(), StringComparison.Ordinal);
        Assert.Equal(JsonValueKind.String, path.GetProperty("output").GetProperty("testSendId").ValueKind);
        Assert.False(listener.Pending());
        Assert.Equal(0, host.Services.GetRequiredService<DeliveryCache>().GetStatus().CommittedRecords);
        Assert.Null(host.Services.GetRequiredService<RuntimeVersionStore>().ReadPointers().EffectiveRevision);
    }

    [Fact]
    public async Task IncompleteDraftCanBeCopiedButNotAppliedAndBindingRunsOutsideTheWriteTransaction()
    {
        using var data = TemporaryDirectory.Create();
        var module = new ExtraReferenceModule();
        await using var host = Host(data.Path, module);
        var seed = Seed(host.Services, "http", 18091);
        module.OnBind = () => Assert.Null(host.Services.GetRequiredService<ConfigDatabase>().Client.Ado.Transaction);
        var node = seed.Graph.Nodes[^1];
        var config = node.Config.Deserialize<RouteModuleOutputDraft>(RouteGraphFormat.Json)! with { Parameters = Json("{}") };
        var graph = seed.Graph with { Nodes = [.. seed.Graph.Nodes[..^1], node with { Config = JsonSerializer.SerializeToElement(config, RouteGraphFormat.Json) }] };
        var store = host.Services.GetRequiredService<RouteDraftStore>();
        var saved = store.Write(null, new(seed.ExpectedVersion, "module_route", "Module", true, graph), "fixture", null);
        var copy = store.Copy(saved.Route.Id, new(saved.Version, "copy", "Copy"), "fixture", null);
        Assert.False(copy.Route.Enabled);
        Assert.Equal(0, module.Calls);
        Assert.False(host.Services.GetRequiredService<RuntimeVersionStore>().ReviewDraft(copy.Version, Token).PreflightPassed);
        var valid = store.Write(saved.Route.Id, new(copy.Version, "module_route", "Module", true, seed.Graph), "fixture", null);
        Assert.True(host.Services.GetRequiredService<RuntimeVersionStore>().ReviewDraft(valid.Version, Token).PreflightPassed);
        Assert.True(module.Calls > 0);
    }

    [Theory]
    [InlineData("{\"address\":\"events\",\"password\":\"fixture-secret\"}")]
    [InlineData("{\"address\":{}}")]
    [InlineData("{\"address\":\"events\",\"address\":\"other\"}")]
    [InlineData("{\"address\":\"events\",\"connection\":\"1\"}")]
    public async Task UnknownNestedDuplicateAndUnseparatedReferencesNeverWriteDrafts(string parameters)
    {
        using var data = TemporaryDirectory.Create();
        await using var host = Host(data.Path, new TestProtocolOutputModule("http"));
        var seed = Seed(host.Services, "http", 18091);
        var output = seed.Graph.Nodes[^1];
        var config = output.Config.Deserialize<RouteModuleOutputDraft>(RouteGraphFormat.Json)! with { Parameters = Json(parameters) };
        var graph = seed.Graph with { Nodes = [.. seed.Graph.Nodes[..^1], output with { Config = JsonSerializer.SerializeToElement(config, RouteGraphFormat.Json) }] };
        var store = host.Services.GetRequiredService<RouteDraftStore>();
        Assert.Throws<ArgumentException>(() => store.Write(null, new(seed.ExpectedVersion, "bad", "Bad", true, graph), "fixture", null));
        Assert.Empty(store.Read().Routes);
        Assert.Equal(seed.ExpectedVersion, store.Read().Version);
    }

    [Theory]
    [InlineData(ConfigurationImportMode.Merge)]
    [InlineData(ConfigurationImportMode.Replace)]
    internal async Task ReferencesAndExactParametersRoundTripWithoutExecutingModulesAndProtectAllConnections(ConfigurationImportMode mode)
    {
        using var data = TemporaryDirectory.Create();
        byte[] file;
        long oldTarget;
        await using (var host = Host(data.Path, new ExtraReferenceModule()))
        {
            var seed = Seed(host.Services, "http", 18091);
            var connections = host.Services.GetRequiredService<ConnectionDraftStore>();
            var extra = connections.Write(null, new(seed.ExpectedVersion, "other", "Other", ConnectionKind.Http, Http: new("http://example.com/")), "fixture", null);
            var output = seed.Graph.Nodes[^1];
            var config = output.Config.Deserialize<RouteModuleOutputDraft>(RouteGraphFormat.Json)!;
            oldTarget = config.ConnectionId!.Value;
            config = config with { Parameters = Json("{\"address\":\"events\",\"precision\":9007199254740993}"), Connections = new(config.Connections!) { ["other"] = extra.Connection.Id } };
            var graph = seed.Graph with { Nodes = [.. seed.Graph.Nodes[..^1], output with { Config = JsonSerializer.SerializeToElement(config, RouteGraphFormat.Json) }] };
            var saved = host.Services.GetRequiredService<RouteDraftStore>().Write(null, new(extra.Version, "module_route", "Module", true, graph), "fixture", null);
            Assert.Throws<DraftWriteException>(() => connections.Delete(extra.Connection.Id, saved.Version, "fixture", null));
            file = host.Services.GetRequiredService<ConfigurationExportStore>().Export(saved.Version, Token);
            var text = Encoding.UTF8.GetString(file);
            Assert.Contains("\"connectionKeys\":{\"connection\":\"sink\",\"other\":\"other\"}", text, StringComparison.Ordinal);
            Assert.DoesNotContain(oldTarget.ToString(System.Globalization.CultureInfo.InvariantCulture), text, StringComparison.Ordinal);
        }
        await using (var missing = new FixedDataDirectoryApplicationFactory(data.Path))
        {
            Assert.Equal(file, missing.Services.GetRequiredService<ConfigurationExportStore>().Export(null, Token));
            Assert.Throws<ConfigurationImportException>(() => missing.Services.GetRequiredService<ConfigurationImportStore>().Prepare(file, mode,
                missing.Services.GetRequiredService<RouteDraftStore>().Read().Version, Token));
        }
        using var other = TemporaryDirectory.Create();
        var module = new ExtraReferenceModule();
        await using var installed = Host(other.Path, module);
        var importer = installed.Services.GetRequiredService<ConfigurationImportStore>();
        var candidate = importer.Prepare(file, mode, "0", Token);
        Assert.Equal(0, module.Calls);
        var imported = Assert.Single(candidate.Draft.Routes).Graph.Nodes[^1].Config.Deserialize<RouteModuleOutputDraft>(RouteGraphFormat.Json)!;
        Assert.NotEqual(oldTarget, imported.ConnectionId);
        Assert.Equal(imported.ConnectionId, imported.Connections!["connection"]);
        Assert.Equal("9007199254740993", imported.Parameters.GetProperty("precision").GetRawText());
        TestAccounts.Create(installed.Services, "editor", ApiRequests.StrongPassword, UserRole.Configurator);
        var actor = TestAccounts.Find(installed.Services, "editor")!;
        var committed = importer.Commit(candidate, new(actor.Id, actor.SecurityStamp, "127.0.0.1"), Token);
        Assert.Equal(0, module.Calls);
        Assert.True(installed.Services.GetRequiredService<RuntimeVersionStore>().ReviewDraft(committed.Version, Token).PreflightPassed);
    }

    private sealed class ExtraReferenceModule : IOutputModuleFactory
    {
        private readonly TestProtocolOutputModule _inner = new("http");
        public string Kind => _inner.Kind;
        public int Version => 1;
        public int Calls => _inner.Calls;
        public Action? OnBind { get; set; }
        public ParameterSchema Parameters { get; } = new("output.extra", ParameterScope.Output,
        [new("connection", "Target", ParameterType.ConnectionReference) { Required = true, ConnectionKinds = ["http"] },
            new("address", "Path", ParameterType.Text) { Required = true, MaximumLength = 1024 },
            new("other", "Other", ParameterType.ConnectionReference) { ConnectionKinds = ["http"] },
            new("precision", "Exact", ParameterType.Number) { WholeNumber = true }]);
        public OutputProtocolBinding BindProtocol(JsonElement parameters, OutputModuleContext context)
        { OnBind?.Invoke(); return _inner.BindProtocol(parameters, context); }
    }
}
