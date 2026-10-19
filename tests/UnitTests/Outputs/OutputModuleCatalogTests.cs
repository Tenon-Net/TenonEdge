using System.Text.Json;
using System.Text.Json.Nodes;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Parameters;
using TenonEdge.Abstractions.Plans;
using TenonEdge.Host.Features.Routes;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Runtime.Connections;
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
                    ? """{"baseAddress":"http://localhost/"}""" : protocol == "mqtt"
                    ? """{"host":"localhost","clientId":"shared"}"""
                    : """{"connectionModule":{"version":1,"parameters":{"host":"localhost","port":19001}}}""")
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

    [Fact]
    public void RegisteredOutputTransportCanBindToNewConnectionKindWithoutChangingTargetOrInputs()
    {
        var module = new Module("wire")
        {
            Config = Json("""{"connectionId":11,"inputs":["meter/source"],"address":"wire://localhost:19001","transportRoute":{"recipient":"wire://localhost:19001","addressTemplate":"events/{deviceKey}"}}"""),
        };
        var connectionSchema = new ParameterSchema("connection.wire", ParameterScope.Connection,
        [
            new("host", "Host", ParameterType.Text) { Required = true, MaximumLength = 253 },
            new("port", "Port", ParameterType.Number) { Required = true, MinimumJson = "1", MaximumJson = "65535", WholeNumber = true },
        ]);
        var connections = new ConnectionModuleCatalog([new ConnectionModuleDefinition("wire", 1, "Wire", connectionSchema, ["host", "port"])]);
        var catalog = new OutputModuleCatalog([module], connections);
        var frozen = catalog.WithFrozenProtocol(Unit("wire"));
        catalog.Validate(frozen);
        var protocol = OutputModuleCatalog.ReadProtocol(frozen)!;
        Assert.Equal("output.wire", protocol.Id.Kind);
        Assert.Equal(11, protocol.Configuration.GetProperty("connectionId").GetInt64());
        Assert.Equal("meter/source", protocol.Configuration.GetProperty("inputs")[0].GetString());
        Assert.Equal("wire://localhost:19001", protocol.Configuration.GetProperty("address").GetString());
        module.Config = Json(module.Config.GetRawText().Replace("events/{deviceKey}", "other/{deviceKey}", StringComparison.Ordinal));
        Assert.Throws<ArgumentException>(() => catalog.Validate(frozen));
        module.Config = Json("""{"connectionId":11,"inputs":["meter/source"]}""");
        Assert.Throws<ArgumentException>(() => catalog.WithFrozenProtocol(Unit("wire")));
        Assert.Equal("events/{deviceKey}", OutputTransportRoute.Read(OutputModuleCatalog.ReadProtocol(frozen)!.Configuration, "output.wire")!.AddressTemplate);
    }

    [Fact]
    public void OutputAuxiliaryConnectionDescendantsKeepOriginalTargetAndJoinHistoryProtection()
    {
        var module = new Module("http")
        {
            Parameters = new("output.test", ParameterScope.Output,
                [new("connection", "Connection", ParameterType.ConnectionReference) { Required = true, ConnectionKinds = ["http"] },
                 new("auxiliary", "Auxiliary", ParameterType.ConnectionReference) { Required = true, ConnectionKinds = ["wrapper"] }])
        };
        var definitions = new ConnectionModuleCatalog([new("wrapper", 1, "Wrapper", new("wrapper", ParameterScope.Connection,
            [new("upstream", "Upstream", ParameterType.ConnectionReference) { Required = true, ConnectionKinds = ["http"] }]))]);
        var source = Change(Unit(), config =>
        {
            config["outputModule"]!["parameters"]!["auxiliary"] = "12";
            config["outputModule"]!["connections"]!["auxiliary"] = JsonNode.Parse("""
                {"connectionId":12,"kind":"wrapper","configuration":{"connectionModule":{"version":1,
                  "parameters":{"upstream":"13"},"connections":{"upstream":{"connectionId":13,"kind":"http",
                  "configuration":{"baseAddress":"http://auxiliary/"}}}}}}
                """);
        });
        var catalog = new OutputModuleCatalog([module], definitions);
        var frozen = catalog.WithFrozenProtocol(source);
        var admitted = RuntimePlanDocument.Parse(Document(frozen), outputs: catalog);
        Assert.Equal(11, OutputModuleCatalog.ReadProtocol(frozen)!.Configuration.GetProperty("connectionId").GetInt64());
        Assert.Equal([11L, 12L, 13L], RuntimeConnectionReferences.ReadIds(frozen).Distinct().Order());
        Assert.Equal(admitted.Canonical, RuntimePlanDocument.ParseStored(admitted.Canonical).Canonical);
        Assert.Throws<RouteTopologyException>(() => new RouteCompilationLimits(Connections: 2).Check(admitted));
        new RouteCompilationLimits(Connections: 3).Check(admitted);
        Assert.Throws<RuntimePlanDocumentException>(() => RuntimePlanDocument.Parse(admitted.Canonical, outputs: new([module])));
        var conflict = new RuntimeUnitPlan(RuntimeUnitId.Create("output.http", "other"),
            Json("""{"connectionId":13,"inputs":["meter/source"],"baseAddress":"http://different/","path":"send"}"""));
        Assert.Throws<RuntimePlanDocumentException>(() => RuntimePlanDocument.ParseStored(Document(frozen, conflict)));
    }

    [Fact]
    public void ExtensionRecipientIdentityIsProtocolBoundAndNeverAcceptsUnrecordedAccounts()
    {
        var identity = OutputRecipientIdentity.Extension("wire", "wire://localhost:19001", "token", "operator");
        Assert.Equal(identity, OutputRecipientIdentity.Read(identity.Freeze()));
        Assert.Null(OutputRecipientIdentity.Read(identity.Freeze().Replace("\"accountRecorded\":true", "\"accountRecorded\":false", StringComparison.Ordinal)));
        Assert.Throws<ArgumentException>(() => OutputRecipientIdentity.Extension("wire", "tcp://localhost:19001", "token", "operator").Freeze());
        Assert.Throws<ArgumentException>(() => OutputRecipientIdentity.Extension("wire", "wire://localhost:19001", "bad auth", "operator").Freeze());
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

    [Theory]
    [InlineData(false, "query")]
    [InlineData(true, "query")]
    [InlineData(false, "encodedQuery")]
    [InlineData(true, "encodedQuery")]
    [InlineData(false, "header")]
    [InlineData(true, "header")]
    public void SecretNamedHttpQueryAndHeadersAreRejectedForNativeAndUninstalledFrozenModules(bool module, string location)
    {
        var catalog = new OutputModuleCatalog([new Module("http")]);
        var frozen = catalog.WithFrozenProtocol(Unit());
        var changed = Change(module ? frozen : OutputModuleCatalog.ReadProtocol(frozen)!, config =>
        {
            var protocol = module ? config["outputModule"]!["protocol"]!["configuration"]! : config;
            if (location == "header")
            { protocol["headers"] = JsonNode.Parse("{\"X-Access-Token\":\"fixture-secret\"}"); }
            else
            { protocol["path"] = location == "query" ? "send?token=fixture-secret" : "send?%61pi_key=fixture-secret"; }
        });
        var error = Assert.Throws<RuntimePlanDocumentException>(() => RuntimePlanDocument.ParseStored(Document(changed)));
        Assert.DoesNotContain("fixture-secret", error.ToString(), StringComparison.Ordinal);
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
