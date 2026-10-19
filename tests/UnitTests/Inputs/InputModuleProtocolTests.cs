using System.Collections.Immutable;
using System.Text.Json;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Abstractions.Parameters;
using TenonEdge.Abstractions.Plans;
using TenonEdge.Host.Features.Routes;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Runtime.Connections;
using TenonEdge.Runtime.Inputs;

namespace TenonEdge.UnitTests.Inputs;

public sealed class InputModuleProtocolTests
{
    private static JsonElement Json(string value) => JsonSerializer.Deserialize<JsonElement>(value);
    private static RuntimeUnitPlan Source(string protocol) => new(RuntimeUnitId.Create("input.protocoltest", "meter/source"),
        JsonSerializer.SerializeToElement(new
        {
            inputModule = new
            {
                version = 1,
                parameters = new { connection = "11" },
                connections = new { connection = new { connectionId = 11, kind = protocol, configuration = Connection(protocol) } }
            }
        }));
    private static JsonElement Connection(string protocol) => Json(protocol switch
    {
        "http" => "{\"baseAddress\":\"http://localhost/\"}",
        "mqtt" => "{\"host\":\"localhost\",\"clientId\":\"module\",\"usePassword\":false}",
        _ => "{\"host\":\"localhost\",\"format\":\"tcp\",\"readTimeoutMs\":1000}",
    });
    private static JsonElement Configuration(string protocol) => Json(protocol switch
    {
        "http" => "{\"connectionId\":11,\"baseAddress\":\"http://localhost/\",\"fields\":[{\"key\":\"value\",\"path\":\"/value\",\"type\":\"integer\"}]}",
        "mqtt" => "{\"connectionId\":11,\"host\":\"localhost\",\"clientId\":\"module\",\"topicFilter\":\"in/#\",\"fields\":[{\"key\":\"value\",\"path\":\"/value\",\"type\":\"integer\"}]}",
        _ => "{\"connectionId\":11,\"host\":\"localhost\",\"readTimeoutMs\":1000,\"unitId\":1,\"points\":[{\"key\":\"value\",\"functionCode\":3,\"address\":0,\"type\":\"uint16\"}]}",
    });
    private static byte[] Document(params RuntimeUnitPlan[] units) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        schemaVersion = 1,
        units = units.Select(unit => new { kind = unit.Id.Kind, key = unit.Id.Key, configuration = unit.Configuration })
    });

    [Theory]
    [InlineData("http")]
    [InlineData("mqtt")]
    [InlineData("modbus")]
    public void DelegatedProtocolIsFrozenAndHistoryRemainsIndependentOfInstalledCode(string protocol)
    {
        var module = new Module(protocol);
        var catalog = new InputModuleCatalog([module]);
        var unit = catalog.WithFrozenFields(Source(protocol));
        var binding = InputModuleCatalog.ReadProtocol(unit)!;
        Assert.Equal("input." + protocol, binding.Id.Kind);
        Assert.Equal("meter/source", binding.Id.Key);
        catalog.Validate(unit);
        var admitted = RuntimePlanDocument.Parse(Document(unit), inputs: catalog);
        Assert.Equal(admitted.Canonical, RuntimePlanDocument.ParseStored(Document(unit)).Canonical);
        var before = module.Calls;
        _ = RuntimePlanDocument.ParseStored(Document(unit));
        Assert.Equal(before, module.Calls);
        module.Config = Json("{\"connectionId\":11,\"changed\":true}");
        Assert.Throws<ArgumentException>(() => catalog.Validate(unit));
        Assert.Equal(admitted.Canonical, RuntimePlanDocument.ParseStored(Document(unit)).Canonical);
    }

    [Theory]
    [InlineData("mqtt")]
    [InlineData("modbus")]
    public void CoupledProtocolsCannotFallBackToUncoordinatedModuleBuild(string protocol)
    {
        var catalog = new InputModuleCatalog([new Module(protocol) { OmitBinding = true }]);
        Assert.Throws<ArgumentException>(() => catalog.WithFrozenFields(Source(protocol)));
        Assert.Throws<RuntimePlanDocumentException>(() => RuntimePlanDocument.ParseStored(Document(Source(protocol))));
    }

    [Theory]
    [InlineData("{\"connectionId\":12}", "input.mqtt")]
    [InlineData("{\"connectionId\":11}", "input.modbus")]
    [InlineData("{\"connectionId\":11}", "input.protocoltest")]
    [InlineData("{\"connectionId\":\"11\"}", "input.mqtt")]
    public void DelegationCannotHideAConnectionOrChainArbitraryModules(string config, string kind)
    {
        var catalog = new InputModuleCatalog([new Module("mqtt") { Config = Json(config), DelegateKind = kind }]);
        Assert.Throws<ArgumentException>(() => catalog.WithFrozenFields(Source("mqtt")));
    }

    [Theory]
    [InlineData("http")]
    [InlineData("mqtt")]
    [InlineData("modbus")]
    public void OriginalProtocolSecretGuardsAlsoApplyToUninstalledModuleHistory(string protocol)
    {
        var config = Configuration(protocol).GetRawText();
        config = config[..^1] + ",\"password\":\"fixture-secret\"}";
        var catalog = new InputModuleCatalog([new Module(protocol) { Config = Json(config) }]);
        var frozen = catalog.WithFrozenFields(Source(protocol));
        var error = Assert.Throws<RuntimePlanDocumentException>(() => RuntimePlanDocument.ParseStored(Document(frozen)));
        Assert.DoesNotContain("fixture-secret", error.ToString(), StringComparison.Ordinal);
        Assert.Null(error.InnerException);
    }

    [Fact]
    public void MqttFeedbackAndSnapshotIdentityGuardsIncludeDelegatedInputs()
    {
        var module = new Module("mqtt");
        var catalog = new InputModuleCatalog([module]);
        var unit = catalog.WithFrozenFields(Source("mqtt"));
        var output = new RuntimeUnitPlan(RuntimeUnitId.Create("output.mqtt", "sink"), Json("{\"connectionId\":11,\"host\":\"localhost\",\"clientId\":\"module\",\"topic\":\"in/value\",\"inputs\":[\"meter/source\"]}"));
        Assert.Throws<RuntimePlanDocumentException>(() => RuntimePlanDocument.ParseStored(Document(unit, output)));
        module.Config = Json(Configuration("mqtt").GetRawText().Replace("localhost", "other", StringComparison.Ordinal));
        Assert.Throws<RuntimePlanDocumentException>(() => RuntimePlanDocument.ParseStored(Document(catalog.WithFrozenFields(Source("mqtt")))));
    }

    [Fact]
    public void ModbusReferencesCountTowardProtocolQuotaAndRejectConflictingPhysicalSettings()
    {
        var module = new Module("modbus");
        var catalog = new InputModuleCatalog([module]);
        var unit = catalog.WithFrozenFields(Source("modbus"));
        var builtin = new RuntimeUnitPlan(RuntimeUnitId.Create("input.modbus", "other/source"),
            Json(Configuration("modbus").GetRawText().Replace(":11,", ":12,", StringComparison.Ordinal)));
        var stored = RuntimePlanDocument.ParseStored(Document(unit, builtin));
        Assert.Throws<RouteTopologyException>(() => new RouteCompilationLimits(ModbusConnections: 1).Check(stored));
        module.Config = Json(Configuration("modbus").GetRawText().Replace("1000", "2000", StringComparison.Ordinal));
        Assert.Throws<RuntimePlanDocumentException>(() => RuntimePlanDocument.ParseStored(Document(catalog.WithFrozenFields(Source("modbus")))));
    }

    [Theory]
    [InlineData("http")]
    [InlineData("mqtt")]
    [InlineData("modbus")]
    public void NestedReferencesDelegateToTheOriginalProtocolAndKeepFrozenHistory(string protocol)
    {
        var module = new Module(protocol, "wrapper");
        var catalog = new InputModuleCatalog([module], WrapperCatalog(protocol));
        var frozen = catalog.WithFrozenFields(NestedSource(protocol));
        catalog.Validate(frozen);
        Assert.Equal("input." + protocol, InputModuleCatalog.ReadProtocol(frozen)!.Id.Kind);
        var admitted = RuntimePlanDocument.Parse(Document(frozen), inputs: catalog);
        Assert.Equal(admitted.Canonical, RuntimePlanDocument.ParseStored(admitted.Canonical).Canonical);
        Assert.Equal([11L, 12L], RuntimeConnectionReferences.ReadIds(frozen).Order());
        Assert.Throws<RuntimePlanDocumentException>(() => RuntimePlanDocument.Parse(admitted.Canonical,
            inputs: new InputModuleCatalog([module])));
    }

    [Theory]
    [InlineData("mqtt")]
    [InlineData("modbus")]
    public void NestedCoupledProtocolsCannotBypassOriginalPlanCoordinationEvenInHistory(string protocol)
    {
        var source = NestedSource(protocol);
        Assert.Throws<RuntimePlanDocumentException>(() => RuntimePlanDocument.ParseStored(Document(source)));
        var catalog = new InputModuleCatalog([new Module(protocol, "wrapper") { OmitBinding = true }], WrapperCatalog(protocol));
        Assert.Throws<ArgumentException>(() => catalog.WithFrozenFields(source));
    }

    private static ConnectionModuleCatalog WrapperCatalog(string protocol) => new([new("wrapper", 1, "Wrapper",
        new("connection.wrapper", ParameterScope.Connection,
            [new("upstream", "Upstream", ParameterType.ConnectionReference) { Required = true, ConnectionKinds = [protocol] }]))]);

    private static RuntimeUnitPlan NestedSource(string protocol)
    {
        var connection = new ModuleConnectionSnapshot(12, "wrapper", JsonSerializer.SerializeToElement(new
        {
            connectionModule = new
            {
                version = 1,
                parameters = new { upstream = "11" },
                connections = new { upstream = new { connectionId = 11, kind = protocol, configuration = Connection(protocol) } }
            }
        }));
        return new(RuntimeUnitId.Create("input.protocoltest", "meter/source"), JsonSerializer.SerializeToElement(new
        {
            inputModule = new
            {
                version = 1,
                parameters = new { connection = "12" },
                connections = new { connection }
            }
        }, JsonSerializerOptions.Web));
    }

    private sealed class Module(string protocol, string? referenceKind = null) : IInputModuleFactory
    {
        public string Kind => "input.protocoltest";
        public int Version => 1;
        public bool UsesDurableHandoff => protocol == "mqtt";
        public string DelegateKind { get; init; } = "input." + protocol;
        public JsonElement Config { get; set; } = Configuration(protocol);
        public bool OmitBinding { get; init; }
        public int Calls { get; private set; }
        public ParameterSchema Parameters { get; } = new("input.protocoltest", ParameterScope.Input,
            [new("connection", "连接", ParameterType.ConnectionReference) { Required = true, ConnectionKinds = [referenceKind ?? protocol] }]);
        public ImmutableArray<InputModuleField> Describe(JsonElement parameters) => [new("value", FieldType.Integer, "count")];
        public InputProtocolBinding? BindProtocol(JsonElement parameters, IReadOnlyDictionary<string, ModuleConnectionSnapshot> connections)
        { Calls++; return OmitBinding ? null : new(DelegateKind, Config); }
        public IRuntimeUnit Build(RuntimeUnitPlan unit, InputUnitContext context) => throw new InvalidOperationException("Must delegate.");
    }
}
