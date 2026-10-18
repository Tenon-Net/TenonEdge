using System.Text.Json;
using System.Text.Json.Nodes;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Parameters;
using TenonEdge.Abstractions.Plans;
using TenonEdge.Host.Features.Routes;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Runtime.Outputs;

namespace TenonEdge.UnitTests.Outputs;

public sealed class OutputModuleCatalogTests
{
    private static readonly string[] Inputs = ["meter/source"];
    private static JsonElement Json(string text) => JsonSerializer.Deserialize<JsonElement>(text);
    private static RuntimeUnitPlan Unit(string protocol = "http") => new(RuntimeUnitId.Create("output.test", "route/out"),
        JsonSerializer.SerializeToElement(new
        {
            connectionId = 11,
            inputs = Inputs,
            outputModule = new
            {
                version = 1,
                parameters = new { connection = "11" },
                connections = new
                {
                    connection = new
                    {
                        connectionId = 11,
                        kind = protocol,
                        configuration = Json(protocol == "http"
                    ? """{"baseAddress":"http://localhost/"}""" : """{"host":"localhost","clientId":"shared"}""")
                    }
                }
            }
        }));
    private static byte[] Document(params RuntimeUnitPlan[] units) => JsonSerializer.SerializeToUtf8Bytes(new
    { schemaVersion = 1, units = units.Select(unit => new { kind = unit.Id.Kind, key = unit.Id.Key, configuration = unit.Configuration }) });
    private static RuntimeUnitPlan Change(RuntimeUnitPlan unit, Action<JsonObject> change)
    {
        var config = JsonNode.Parse(unit.Configuration.GetRawText())!.AsObject();
        change(config);
        return new(unit.Id, JsonSerializer.SerializeToElement(config));
    }

    [Theory]
    [InlineData("http")]
    [InlineData("mqtt")]
    public void ExactProtocolIsFrozenAndHistoryDoesNotExecuteInstalledCode(string protocol)
    {
        var module = new Module(protocol);
        var catalog = new OutputModuleCatalog([module]);
        var frozen = catalog.WithFrozenProtocol(Unit(protocol));
        catalog.Validate(frozen);
        var plan = RuntimePlanDocument.Parse(Document(frozen), outputs: catalog);
        Assert.Equal("output." + protocol, OutputModuleCatalog.ReadProtocol(frozen)!.Id.Kind);
        Assert.Equal("route/out", OutputModuleCatalog.ReadProtocol(frozen)!.Id.Key);
        var calls = module.Calls;
        Assert.Equal(plan.Canonical, RuntimePlanDocument.ParseStored(Document(frozen)).Canonical);
        Assert.Equal(calls, module.Calls);
        module.Throw = true;
        Assert.Equal(plan.Canonical, RuntimePlanDocument.ParseStored(Document(frozen)).Canonical);
        var error = Assert.Throws<ArgumentException>(() => catalog.Validate(frozen));
        Assert.DoesNotContain("fixture-secret", error.ToString(), StringComparison.Ordinal);
        Assert.Null(error.InnerException);
        Assert.Throws<RuntimePlanDocumentException>(() => RuntimePlanDocument.Parse(Document(frozen)));
        Assert.Throws<RuntimePlanDocumentException>(() => plan.ValidateOutputs(OutputModuleCatalog.Empty));
    }

    [Theory]
    [InlineData("output.http")]
    [InlineData("output.mqtt")]
    [InlineData("output.tenon")]
    [InlineData("input.test")]
    [InlineData("output.bad/key")]
    public void RegistrationCannotReplaceBuiltinsOrInvalidKinds(string kind) =>
        Assert.Throws<ArgumentException>(() => new OutputModuleCatalog([new Module("http") { Kind = kind }]));

    [Fact]
    public void DuplicateVersionsSecretsWrongScopeAndInvalidVersionAreRejected()
    {
        Assert.Throws<ArgumentException>(() => new OutputModuleCatalog([new Module("http"), new Module("http")]));
        Assert.Throws<ArgumentException>(() => new OutputModuleCatalog([new Module("http") { Version = 0 }]));
        Assert.Throws<ArgumentException>(() => new OutputModuleCatalog([new Module("http")
        { Parameters = new("test", ParameterScope.Connection, [new("secret", "Secret", ParameterType.Credential)]) }]));
        Assert.Equal(2, new OutputModuleCatalog([new Module("http"), new Module("http") { Version = 2 }]).Modules.Length);
    }

    [Theory]
    [InlineData("target")]
    [InlineData("inputs")]
    [InlineData("mapping")]
    [InlineData("mappingStages")]
    [InlineData("transformStages")]
    [InlineData("queueOnly")]
    [InlineData("protocol")]
    [InlineData("oversize")]
    public void BindingCannotChangeTargetInputsProtocolOrHideTransformStages(string invalid)
    {
        var module = new Module("http");
        var config = JsonNode.Parse(module.Config.GetRawText())!.AsObject();
        switch (invalid)
        {
            case "target":
                config["connectionId"] = 12;
                break;
            case "inputs":
                config["inputs"] = new JsonArray("other/source");
                break;
            case "protocol":
                module.Protocol = "mqtt";
                break;
            case "oversize":
                config["large"] = new string('x', 1024 * 1024);
                break;
            default:
                config[invalid] = true;
                break;
        }
        module.Config = JsonSerializer.SerializeToElement(config);
        Assert.Throws<ArgumentException>(() => new OutputModuleCatalog([module]).WithFrozenProtocol(Unit()));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("version")]
    [InlineData("missingConnection")]
    [InlineData("reference")]
    [InlineData("duplicateInput")]
    [InlineData("parameter")]
    [InlineData("nestedParameter")]
    public void InvalidEnvelopeAndReferencesCannotEnterCatalog(string invalid)
    {
        var unit = Change(Unit(), config =>
        {
            var envelope = config["outputModule"]!;
            switch (invalid)
            {
                case "unknown":
                    config["hidden"] = true;
                    break;
                case "version":
                    envelope["version"] = 2;
                    break;
                case "missingConnection":
                    envelope["connections"] = new JsonObject();
                    break;
                case "reference":
                    envelope["parameters"]!["connection"] = "12";
                    break;
                case "duplicateInput":
                    config["inputs"] = new JsonArray("meter/source", "meter/source");
                    break;
                case "parameter":
                    envelope["parameters"]!["password"] = "fixture-secret";
                    break;
                case "nestedParameter":
                    envelope["parameters"]!["connection"] = new JsonObject();
                    break;
            }
        });
        Assert.Throws<ArgumentException>(() => new OutputModuleCatalog([new Module("http")]).WithFrozenProtocol(unit));
    }

    [Theory]
    [InlineData("http")]
    [InlineData("mqtt")]
    public void OriginalProtocolGuardsRejectSecretsEvenWhenModuleIsUninstalled(string protocol)
    {
        var catalog = new OutputModuleCatalog([new Module(protocol)]);
        var unit = catalog.WithFrozenProtocol(Unit(protocol));
        foreach (var location in new[] { "protocol", "connections" })
        {
            var changed = Change(unit, config =>
            {
                var envelope = config["outputModule"]!;
                var section = location == "protocol" ? envelope[location] : envelope[location]!["connection"];
                section!["configuration"]!["password"] = "fixture-secret";
            });
            var error = Assert.Throws<RuntimePlanDocumentException>(() => RuntimePlanDocument.ParseStored(Document(changed)));
            Assert.DoesNotContain("fixture-secret", error.ToString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void BindingChangeMissingFrozenBindingAndConflictingConnectionAreRejected()
    {
        var module = new Module("http");
        var catalog = new OutputModuleCatalog([module]);
        var unit = catalog.WithFrozenProtocol(Unit());
        module.Config = Json(module.Config.GetRawText().Replace("send", "changed", StringComparison.Ordinal));
        Assert.Throws<ArgumentException>(() => catalog.Validate(unit));
        Assert.Throws<RuntimePlanDocumentException>(() => RuntimePlanDocument.ParseStored(Document(Unit())));
        module.Config = Json(module.Config.GetRawText().Replace("localhost", "other", StringComparison.Ordinal));
        Assert.Throws<RuntimePlanDocumentException>(() => RuntimePlanDocument.ParseStored(Document(catalog.WithFrozenProtocol(Unit()))));
    }

    [Fact]
    public void FeedbackAndRouteQuotaUseFrozenProtocolNotModuleKind()
    {
        var catalog = new OutputModuleCatalog([new Module("mqtt")]);
        var unit = catalog.WithFrozenProtocol(Unit("mqtt"));
        var input = new RuntimeUnitPlan(RuntimeUnitId.Create("input.mqtt", "meter/source"), Json("""
            {"connectionId":11,"host":"localhost","clientId":"shared","topicFilter":"out/#","fields":[{"key":"v","path":"/v","type":"integer"}]}
            """));
        Assert.Throws<RuntimePlanDocumentException>(() => RuntimePlanDocument.ParseStored(Document(unit, input)));
        var second = new RuntimeUnitPlan(RuntimeUnitId.Create("output.test", "route/second"), unit.Configuration);
        var plan = RuntimePlanDocument.ParseStored(Document(unit, second));
        Assert.Throws<RouteTopologyException>(() => new RouteCompilationLimits(PathsPerSource: 1).Check(plan));
    }

    private sealed class Module(string protocol) : IOutputModuleFactory
    {
        public string Kind { get; init; } = "output.test";
        public int Version { get; init; } = 1;
        public bool Throw { get; set; }
        public int Calls { get; private set; }
        public string Protocol { get; set; } = protocol;
        public ParameterSchema Parameters { get; init; } = new("output.test", ParameterScope.Output,
            [new("connection", "Connection", ParameterType.ConnectionReference) { Required = true, ConnectionKinds = [protocol] }]);
        public JsonElement Config { get; set; } = Json(protocol == "http"
            ? """{"connectionId":11,"inputs":["meter/source"],"baseAddress":"http://localhost/","path":"send"}"""
            : """{"connectionId":11,"inputs":["meter/source"],"host":"localhost","clientId":"shared","topic":"out/value"}""");
        public OutputProtocolBinding BindProtocol(JsonElement parameters, OutputModuleContext context)
        {
            Calls++;
            if (Throw)
            { throw new InvalidOperationException("fixture-secret"); }
            return new("output." + Protocol, Config);
        }
    }
}
