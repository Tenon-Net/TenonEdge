using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Parameters;
using TenonEdge.Host.Features.Configuration.Transfer;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Routes;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Security;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.Runtime.Deliveries;

namespace TenonEdge.IntegrationTests.Routes;

public sealed partial class RouteOutputModuleTests
{
    [Theory]
    [InlineData("missing")]
    [InlineData("version")]
    public async Task UninstalledKindsAndVersionsCannotBeSavedOrCompiled(string scenario)
    {
        using var data = TemporaryDirectory.Create();
        await using var host = Host(data.Path, new TestProtocolOutputModule("http"));
        var seed = Seed(host.Services, "http", 18091);
        var node = seed.Graph.Nodes[^1];
        var graph = seed.Graph with
        {
            Nodes = [.. seed.Graph.Nodes[..^1], node with
        { Type = scenario == "missing" ? "output.uninstalled" : node.Type, Version = scenario == "version" ? 2 : 1 }]
        };
        var store = host.Services.GetRequiredService<RouteDraftStore>();
        Assert.Throws<ArgumentException>(() => store.Write(null, new(seed.ExpectedVersion, "bad", "Bad", true, graph), "fixture", null));
        var snapshot = store.ReadGraphCompilation(seed.ExpectedVersion, seed.Graph, Token);
        Assert.Throws<RouteTopologyException>(() => RoutePlanCompiler.Compile(snapshot with { Routes = [snapshot.Routes[0] with { Graph = graph }] }, Token));
        Assert.Empty(store.Read().Routes);
        Assert.Equal(seed.ExpectedVersion, store.Read().Version);
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("missing")]
    [InlineData("wrongProtocol")]
    [InlineData("notTarget")]
    public async Task ExplicitConnectionFailuresRejectCompilationAndDoNotPublishPreviewArtifacts(string scenario)
    {
        using var data = TemporaryDirectory.Create();
        await using var host = Host(data.Path, new ExtraReferenceModule());
        var seed = Seed(host.Services, "http", 18091);
        var config = seed.Graph.Nodes[^1].Config.Deserialize<RouteModuleOutputDraft>(RouteGraphFormat.Json)!;
        var store = host.Services.GetRequiredService<ConnectionDraftStore>();
        var extra = store.Write(null, new(seed.ExpectedVersion, "extra", "Extra",
            scenario == "wrongProtocol" ? ConnectionKind.Mqtt : ConnectionKind.Http, Enabled: scenario != "disabled",
            Http: scenario != "wrongProtocol" ? new("http://example.com/") : null,
            Mqtt: scenario == "wrongProtocol" ? new("example.com", 1883) : null), "fixture", null);
        config = config with
        {
            Connections = scenario == "notTarget" ? new() { ["connection"] = extra.Connection.Id }
            : new(config.Connections!) { ["other"] = scenario == "missing" ? 1 : extra.Connection.Id }
        };
        var request = seed with { ExpectedVersion = extra.Version, Graph = WithConfig(seed.Graph, JsonSerializer.SerializeToElement(config, RouteGraphFormat.Json)) };
        var routes = host.Services.GetRequiredService<RouteDraftStore>();
        var saved = routes.Write(null, new(request.ExpectedVersion, "module_route", "Module", true, request.Graph), "fixture", null);
        Assert.False(host.Services.GetRequiredService<RuntimeVersionStore>().ReviewDraft(saved.Version, Token).PreflightPassed);
        await AssertPreviewRejected(host, request with { ExpectedVersion = saved.Version });
    }

    [Theory]
    [InlineData("duplicateConnections")]
    [InlineData("duplicateTarget")]
    [InlineData("parameterBytes")]
    [InlineData("parameterCount")]
    [InlineData("invalidReference")]
    public async Task DuplicateAndOversizedConfigurationNeverChangesDraft(string scenario)
    {
        using var data = TemporaryDirectory.Create();
        await using var host = Host(data.Path, new TestProtocolOutputModule("http"));
        var seed = Seed(host.Services, "http", 18091);
        var id = seed.Graph.Nodes[^1].Config.GetProperty("connectionId").GetInt64();
        var raw = scenario switch
        {
            "duplicateConnections" => $$$"""{"connectionId":{{{id}}},"parameters":{"address":"events"},"connections":{"connection":{{{id}}},"connection":1}}""",
            "duplicateTarget" => $$$"""{"connectionId":{{{id}}},"connectionId":1,"parameters":{"address":"events"},"connections":{"connection":{{{id}}}}}""",
            "parameterBytes" => JsonSerializer.Serialize(new { connectionId = id, parameters = new { address = new string('x', ParameterSchema.MaximumConfigurationBytes) } }),
            "parameterCount" => JsonSerializer.Serialize(new { connectionId = id, parameters = Enumerable.Range(0, ParameterSchema.MaximumParameters + 1).ToDictionary(index => "p" + index, _ => 1) }),
            _ => $$$"""{"connectionId":{{{id}}},"parameters":{"address":"events"},"connections":{"connection":9007199254740992}}""",
        };
        var store = host.Services.GetRequiredService<RouteDraftStore>();
        Assert.Throws<ArgumentException>(() => store.Write(null, new(seed.ExpectedVersion, "bad", "Bad", true, WithConfig(seed.Graph, Json(raw))), "fixture", null));
        Assert.Empty(store.Read().Routes);
        Assert.Equal(seed.ExpectedVersion, store.Read().Version);
    }

    [Theory]
    [InlineData("secretQuery")]
    [InlineData("secretHeader")]
    [InlineData("namedSecretHeader")]
    [InlineData("missingField")]
    [InlineData("changedRecipient")]
    [InlineData("throws")]
    public async Task InvalidBindingFailsPreflightAndPreviewWithoutLeakingSecretsOrCapturingSend(string scenario)
    {
        using var data = TemporaryDirectory.Create();
        await using var host = Host(data.Path, new InvalidBindingModule(scenario));
        var seed = Seed(host.Services, "http", 18091);
        var routes = host.Services.GetRequiredService<RouteDraftStore>();
        var saved = routes.Write(null, new(seed.ExpectedVersion, "module_route", "Module", true, seed.Graph), "fixture", null);
        Assert.Throws<RouteTopologyException>(() => RoutePlanCompiler.Compile(routes.ReadCompilation(saved.Version, Token), Token));
        Assert.False(host.Services.GetRequiredService<RuntimeVersionStore>().ReviewDraft(saved.Version, Token).PreflightPassed);
        await AssertPreviewRejected(host, seed with { ExpectedVersion = saved.Version });
    }

    [Theory]
    [InlineData("connections")]
    [InlineData("Connections")]
    [InlineData("connectionId")]
    [InlineData("ConnectionId")]
    [InlineData("sourceIds")]
    [InlineData("SourceIds")]
    [InlineData("numericKey")]
    [InlineData("missingKey")]
    [InlineData("wrongProtocol")]
    public async Task PortableImportRejectsInternalAliasesAndInvalidKeysAtomicallyWithoutBinding(string scenario)
    {
        using var data = TemporaryDirectory.Create();
        var module = new TestProtocolOutputModule("http");
        await using var host = Host(data.Path, module);
        var seed = Seed(host.Services, "http", 18091);
        var extra = host.Services.GetRequiredService<ConnectionDraftStore>().Write(null,
            new(seed.ExpectedVersion, "mqtt", "MQTT", ConnectionKind.Mqtt, Mqtt: new("example.com", 1883)), "fixture", null);
        var routes = host.Services.GetRequiredService<RouteDraftStore>();
        var saved = routes.Write(null, new(extra.Version, "module_route", "Module", true, seed.Graph), "fixture", null);
        var exporter = host.Services.GetRequiredService<ConfigurationExportStore>();
        var original = exporter.Export(saved.Version, Token);
        var document = JsonNode.Parse(original)!;
        var config = document["routes"]![0]!["graph"]!["nodes"]!.AsArray()[^1]!["config"]!;
        if (scenario is "numericKey" or "missingKey" or "wrongProtocol")
        { config["connectionKeys"]!["connection"] = scenario == "numericKey" ? JsonValue.Create(1) : JsonValue.Create(scenario == "missingKey" ? "missing" : "mqtt"); }
        else
        { config[scenario] = scenario.EndsWith("Id", StringComparison.Ordinal) ? JsonValue.Create(1) : JsonNode.Parse("{\"connection\":1}"); }
        foreach (var mode in new[] { ConfigurationImportMode.Merge, ConfigurationImportMode.Replace })
        {
            Assert.Throws<ConfigurationImportException>(() => host.Services.GetRequiredService<ConfigurationImportStore>()
                .Prepare(JsonSerializer.SerializeToUtf8Bytes(document), mode, saved.Version, Token));
            Assert.Equal(original, exporter.Export(saved.Version, Token));
        }
        Assert.Equal(0, module.Calls);
    }

    private static RouteGraph WithConfig(RouteGraph graph, JsonElement config) => graph with
    { Nodes = [.. graph.Nodes[..^1], graph.Nodes[^1] with { Config = config }] };

    private static async Task AssertPreviewRejected(FixedDataDirectoryApplicationFactory host, RoutePreviewRequest request)
    {
        TestAccounts.Create(host.Services, "editor", ApiRequests.StrongPassword, UserRole.Configurator);
        using var client = await host.CreateLoggedInClientAsync("editor", Token);
        using var response = await client.PostJsonAsync("/api/edge-admin/v1/routes/preview", request, Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.ReadJsonAsync(Token);
        Assert.DoesNotContain("fixture-secret", body.ToString(), StringComparison.Ordinal);
        var path = Assert.Single(body.GetProperty("result").GetProperty("paths").EnumerateArray());
        Assert.False(path.GetProperty("succeeded").GetBoolean());
        Assert.Equal(JsonValueKind.Null, path.GetProperty("output").ValueKind);
        Assert.NotEmpty(path.GetProperty("issues").EnumerateArray());
        Assert.Equal(0, host.Services.GetRequiredService<DeliveryCache>().GetStatus().CommittedRecords);
        Assert.Null(host.Services.GetRequiredService<RuntimeVersionStore>().ReadPointers().EffectiveRevision);
    }

    private sealed class InvalidBindingModule(string scenario) : IOutputModuleFactory
    {
        private readonly TestProtocolOutputModule _inner = new("http");
        public string Kind => _inner.Kind;
        public int Version => 1;
        public ParameterSchema Parameters => _inner.Parameters;
        public OutputProtocolBinding BindProtocol(JsonElement parameters, OutputModuleContext context)
        {
            if (scenario == "throws")
            { throw new InvalidOperationException("fixture-secret"); }
            var binding = _inner.BindProtocol(parameters, context);
            var config = JsonNode.Parse(binding.Configuration.GetRawText())!;
            switch (scenario)
            {
                case "secretQuery":
                    config["path"] = "events?token=fixture-secret";
                    break;
                case "secretHeader":
                    config["headers"] = JsonNode.Parse("{\"Authorization\":\"fixture-secret\"}");
                    break;
                case "namedSecretHeader":
                    config["headers"] = JsonNode.Parse("{\"X-Access-Token\":\"fixture-secret\"}");
                    break;
                case "missingField":
                    config["payload"] = JsonNode.Parse("{\"mode\":\"custom\",\"fields\":[{\"target\":\"x\",\"sourceField\":\"missing\"}]}");
                    break;
                case "changedRecipient":
                    config["baseAddress"] = "http://different.invalid/";
                    break;
            }
            return binding with { Configuration = JsonSerializer.SerializeToElement(config) };
        }
    }
}
