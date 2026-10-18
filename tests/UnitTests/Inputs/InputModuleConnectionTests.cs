using System.Collections.Immutable;
using System.Text.Json;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Abstractions.Parameters;
using TenonEdge.Abstractions.Plans;
using TenonEdge.Host.Features.Routes;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Runtime.Inputs;

namespace TenonEdge.UnitTests.Inputs;

public sealed class InputModuleConnectionTests
{
    [Fact]
    public void ReferencesAreExplicitPerParameterAndFrozenWithoutExecutingTheModuleOnHistoryRead()
    {
        var factory = new Factory();
        var catalog = new InputModuleCatalog([factory]);
        var unit = Unit("{\"primary\":\"11\",\"backup\":\"12\",\"value\":9007199254740993}",
            "{\"primary\":" + Connection(11) + ",\"backup\":" + Connection(12) + "}");
        var frozen = catalog.WithFrozenFields(unit);
        Assert.Equal(1, factory.Descriptions);
        Assert.Equal("9007199254740993", factory.Observed.GetProperty("value").GetRawText());
        var admitted = RuntimePlanDocument.Parse(Document(frozen), inputs: catalog);
        var reads = factory.Descriptions;
        var stored = RuntimePlanDocument.ParseStored(admitted.Canonical);
        Assert.Equal(reads, factory.Descriptions);
        Assert.Equal(admitted.Canonical, stored.Canonical);
        Assert.Equal(new long[] { 11, 12 }, RuntimeConnectionReferences.ReadIds(stored.Units[0]).Order());
        Assert.Equal(11, InputModuleCatalog.ReadConnections(frozen)["primary"].ConnectionId);
        Assert.Single(InputModuleCatalog.ReadFrozenFields(frozen)!.Value);
        Assert.Throws<RuntimePlanDocumentException>(() => RuntimePlanDocument.Parse(stored.Canonical));
    }

    [Theory]
    [InlineData("{\"primary\":\"11\"}", "{}")]
    [InlineData("{\"primary\":\"12\"}", "{\"primary\":CONNECTION}")]
    [InlineData("{\"primary\":11}", "{\"primary\":CONNECTION}")]
    [InlineData("{\"primary\":\"011\"}", "{\"primary\":CONNECTION}")]
    [InlineData("{\"primary\":null}", "{\"primary\":CONNECTION}")]
    [InlineData("{}", "{\"primary\":CONNECTION}")]
    [InlineData("{\"primary\":\"11\"}", "{\"extra\":CONNECTION}")]
    [InlineData("{\"primary\":\"11\",\"backup\":\"11\"}", "{\"primary\":CONNECTION}")]
    public void InvalidOrUndeclaredBindingsAreRejectedBeforeDescribe(string parameters, string connections)
    {
        var factory = new Factory();
        var catalog = new InputModuleCatalog([factory]);
        var unit = Unit(parameters, connections.Replace("CONNECTION", Connection(11), StringComparison.Ordinal));
        var error = Assert.Throws<ArgumentException>(() => catalog.Describe(unit));
        Assert.Null(error.InnerException);
        Assert.Equal(0, factory.Descriptions);
    }

    [Fact]
    public void WrongProtocolDoesNotReachDescribeAndOptionalNullDoesNotRequireABinding()
    {
        var factory = new Factory();
        var catalog = new InputModuleCatalog([factory]);
        Assert.Throws<ArgumentException>(() => catalog.Describe(Unit("{\"primary\":\"11\"}",
            "{\"primary\":" + Connection(11, kind: "mqtt") + "}")));
        Assert.Equal(0, factory.Descriptions);
        Assert.Single(catalog.Describe(Unit("{\"primary\":\"11\",\"backup\":null}", "{\"primary\":" + Connection(11) + "}")));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"primary\":null}")]
    [InlineData("{\"primary\":{\"connectionId\":\"11\",\"kind\":\"http\",\"configuration\":{}}}")]
    [InlineData("{\"primary\":{\"connectionId\":0,\"kind\":\"http\",\"configuration\":{}}}")]
    [InlineData("{\"primary\":{\"connectionId\":9007199254740992,\"kind\":\"http\",\"configuration\":{}}}")]
    [InlineData("{\"primary\":{\"connectionId\":11,\"kind\":\"http\",\"configuration\":[],\"secret\":\"fixture-secret\"}}")]
    public void BadSnapshotStructureIsRejectedEvenWhenTheModuleIsUninstalled(string connections)
    {
        var unit = Unit("{\"primary\":\"11\"}", connections);
        var error = Assert.Throws<ArgumentException>(() => InputModuleCatalog.ValidateStoredShape(unit));
        Assert.DoesNotContain("fixture-secret", error.ToString(), StringComparison.Ordinal);
        Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData("{\"baseAddress\":\"http://localhost/\",\"password\":\"fixture-secret\"}")]
    [InlineData("{\"baseAddress\":\"http://user:fixture-secret@localhost/\"}")]
    [InlineData("{\"baseAddress\":\"http://localhost/?token=fixture-secret\"}")]
    [InlineData("{\"baseAddress\":\"http://localhost/\",\"headers\":{\"X-Secret\":\"fixture-secret\"}}")]
    [InlineData("{\"baseAddress\":\"http://localhost/\",\"headers\":{\"Authorization\":\"fixture-secret\"}}")]
    [InlineData("{\"baseAddress\":\"http://localhost/\",\"timeoutMs\":1}")]
    [InlineData("{\"baseAddress\":\"http://localhost/\",\"path\":\"fixture-secret\"}")]
    public void HostRejectsSecretUnknownAndInvalidProtocolSettingsBeforePersisting(string config)
    {
        var unit = Unit("{\"primary\":\"11\"}", "{\"primary\":" + Connection(11, config) + "}");
        var error = Assert.Throws<RuntimePlanDocumentException>(() => RuntimePlanDocument.ParseStored(Document(unit)));
        Assert.DoesNotContain("fixture-secret", error.ToString(), StringComparison.Ordinal);
        Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData("mqtt")]
    [InlineData("modbus")]
    [InlineData("unknown")]
    public void ProtocolsWithoutPlanLevelModuleCoordinationAreNotSilentlyEnabled(string kind)
    {
        var unit = Unit("{\"primary\":\"11\"}", "{\"primary\":" + Connection(11, kind: kind) + "}");
        Assert.Throws<RuntimePlanDocumentException>(() => RuntimePlanDocument.ParseStored(Document(unit)));
    }

    [Fact]
    public void AllModuleReferencesCountTowardTheSameLimitsAndConflictWithBuiltinIdentities()
    {
        var unit = Unit("{\"primary\":\"11\",\"backup\":\"12\"}",
            "{\"primary\":" + Connection(11) + ",\"backup\":" + Connection(12) + "}");
        var plan = RuntimePlanDocument.ParseStored(Document(unit));
        Assert.Throws<RouteTopologyException>(() => new RouteCompilationLimits(Connections: 1).Check(plan));
        new RouteCompilationLimits(Connections: 2).Check(plan);
        var other = new RuntimeUnitPlan(RuntimeUnitId.Create("output.http", "sink"), Json("""
            {"connectionId":11,"baseAddress":"http://different/","inputs":["meter/source"]}
            """));
        Assert.Throws<RuntimePlanDocumentException>(() => RuntimePlanDocument.ParseStored(Document(unit, other)));
        var same = new RuntimeUnitPlan(other.Id, Json("""
            {"connectionId":11,"baseAddress":"http://localhost/","inputs":["meter/source"]}
            """));
        new RouteCompilationLimits(Connections: 2).Check(RuntimePlanDocument.ParseStored(Document(unit, same)));
        var conflict = Unit("{\"primary\":\"11\",\"backup\":\"11\"}",
            "{\"primary\":" + Connection(11) + ",\"backup\":" + Connection(11, "{\"baseAddress\":\"http://different/\"}") + "}");
        Assert.Throws<RuntimePlanDocumentException>(() => RuntimePlanDocument.ParseStored(Document(conflict)));
    }

    [Fact]
    public void ReferenceCountAndBytesAreBoundedBeforeModuleExecution()
    {
        var tooMany = "{" + string.Join(',', Enumerable.Range(1, 129).Select(i => "\"c" + i + "\":" + Connection(i))) + "}";
        Assert.Throws<ArgumentException>(() => InputModuleCatalog.ValidateStoredShape(Unit("{}", tooMany)));
        var oversized = "{\"primary\":" + Connection(11, JsonSerializer.Serialize(new { text = new string('x', ParameterSchema.MaximumConfigurationBytes) })) + "}";
        Assert.Throws<ArgumentException>(() => InputModuleCatalog.ValidateStoredShape(Unit("{}", oversized)));
    }

    [Theory]
    [InlineData(500, true)]
    [InlineData(501, false)]
    public void ModuleOnlyConnectionsCannotBypassTheGlobalPlanLimit(int count, bool valid)
    {
        var units = Enumerable.Range(1, count).Chunk(100).Select((ids, index) => new RuntimeUnitPlan(
            RuntimeUnitId.Create("input.testreference", "meter/source" + index), JsonSerializer.SerializeToElement(new
            {
                inputModule = new
                {
                    version = 1,
                    parameters = ids.ToDictionary(id => "c" + id, id => id.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    connections = ids.ToDictionary(id => "c" + id, id => Json(Connection(id))),
                },
            }))).ToArray();
        if (valid)
        { new RouteCompilationLimits().Check(RuntimePlanDocument.ParseStored(Document(units))); }
        else
        { Assert.Throws<RuntimePlanDocumentException>(() => RuntimePlanDocument.ParseStored(Document(units))); }
    }

    private static JsonElement Json(string value) => JsonSerializer.Deserialize<JsonElement>(value);
    private static string Connection(long id, string config = "{\"baseAddress\":\"http://localhost/\"}", string kind = "http") =>
        JsonSerializer.Serialize(new { connectionId = id, kind, configuration = Json(config) });
    private static RuntimeUnitPlan Unit(string parameters, string connections) => new(RuntimeUnitId.Create("input.testreference", "meter/source"),
        JsonSerializer.SerializeToElement(new { inputModule = new { version = 1, parameters = Json(parameters), connections = Json(connections) } }));
    private static byte[] Document(params RuntimeUnitPlan[] units) => JsonSerializer.SerializeToUtf8Bytes(new
    { schemaVersion = 1, units = units.Select(unit => new { kind = unit.Id.Kind, key = unit.Id.Key, configuration = unit.Configuration }) });

    private sealed class Factory : IInputModuleFactory
    {
        public string Kind => "input.testreference";
        public int Version => 1;
        public int Descriptions { get; private set; }
        public JsonElement Observed { get; private set; }
        public ParameterSchema Parameters { get; } = new("input.testreference", ParameterScope.Input,
            [new("primary", "主要连接", ParameterType.ConnectionReference) { Required = true, ConnectionKinds = ["http"] },
                new("backup", "备用连接", ParameterType.ConnectionReference) { ConnectionKinds = ["http"] },
                new("value", "值", ParameterType.Number) { WholeNumber = true }]);
        public ImmutableArray<InputModuleField> Describe(JsonElement parameters)
        {
            Observed = parameters.Clone();
            Descriptions++;
            return [new("value", FieldType.Integer, "count")];
        }
        public IRuntimeUnit Build(RuntimeUnitPlan unit, InputUnitContext context) => throw new InvalidOperationException("Metadata cannot build inputs.");
    }
}
