using System.Collections.Immutable;
using System.Text.Json;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Parameters;
using TenonEdge.Abstractions.Plans;
using TenonEdge.Host.Features.Routes;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Runtime.Connections;
using TenonEdge.Runtime.Inputs;

namespace TenonEdge.UnitTests.Connections;

public sealed class ConnectionModuleCatalogTests
{
    private static JsonElement Json(string text) => JsonSerializer.Deserialize<JsonElement>(text);
    private static ConnectionModuleDefinition Definition(int version = 1, string kind = "testwire") => new(kind, version, "Test wire",
        new("connection.testwire", ParameterScope.Connection,
            [new("offset", "Offset", ParameterType.Number) { Required = true, WholeNumber = true, MinimumJson = "0", MaximumJson = "18446744073709551615" }]));

    [Fact]
    public void RegisteredVersionsFreezeExactParametersAndHistoryDoesNotRequireTheDirectory()
    {
        var catalog = new ConnectionModuleCatalog([Definition(2), Definition()]);
        Assert.Equal([1, 2], catalog.Modules.Select(value => value.Version));
        var snapshot = catalog.Freeze(1, "testwire", 1, Json("{\"offset\":9007199254740993}"));
        var read = ModuleConnectionSnapshot.ReadModuleConfiguration(snapshot);
        Assert.Equal(1, read.Version);
        Assert.Equal("9007199254740993", read.Parameters.GetProperty("offset").GetRawText());
        var identity = ModuleConnectionSnapshot.ModuleConfigurationIdentity(snapshot);
        Assert.Equal(identity, ModuleConnectionSnapshot.ModuleConfigurationIdentity(new(2, snapshot.Kind, snapshot.Configuration)));
        Assert.NotEqual(identity, ModuleConnectionSnapshot.ModuleConfigurationIdentity(catalog.Freeze(1, "testwire", 2, read.Parameters)));
        Assert.NotEqual(identity, ModuleConnectionSnapshot.ModuleConfigurationIdentity(catalog.Freeze(1, "testwire", 1, Json("{\"offset\":9007199254740992}"))));
        Assert.Throws<ArgumentException>(() => ConnectionModuleCatalog.Empty.Validate(snapshot));
        Assert.Equal(read, ModuleConnectionSnapshot.ReadModuleConfiguration(snapshot));
    }

    [Theory]
    [InlineData("http")]
    [InlineData("mqtt")]
    [InlineData("modbus")]
    [InlineData("tenon")]
    [InlineData("Bad")]
    [InlineData("test-wire")]
    [InlineData("test..wire")]
    public void CannotReplaceBuiltinsOrUseAmbiguousKinds(string kind) =>
        Assert.Throws<ArgumentException>(() => Definition(kind: kind));

    [Fact]
    public void DuplicateRegistrationInvalidVersionScopeAndDisplayAreRejected()
    {
        Assert.Throws<ArgumentException>(() => new ConnectionModuleCatalog([Definition(), Definition()]));
        Assert.Throws<ArgumentException>(() => Definition(0));
        Assert.Throws<ArgumentException>(() => new ConnectionModuleDefinition("testwire", 1, "\n", Definition().Parameters));
        Assert.Throws<ArgumentException>(() => new ConnectionModuleDefinition("testwire", 1, "Test",
            new("test", ParameterScope.Input, [new("x", "X", ParameterType.Switch)])));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"offset\":-1}")]
    [InlineData("{\"offset\":1.1}")]
    [InlineData("{\"offset\":\"1\"}")]
    [InlineData("{\"offset\":1,\"offset\":2}")]
    [InlineData("{\"offset\":1,\"password\":\"fixture-secret\"}")]
    [InlineData("{\"offset\":[]}")]
    public void BadParametersAreRejectedBeforeFreezingWithoutEchoingTheirContents(string parameters)
    {
        var error = Assert.Throws<ArgumentException>(() => new ConnectionModuleCatalog([Definition()]).Freeze(1, "testwire", 1, Json(parameters)));
        Assert.DoesNotContain("fixture-secret", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ASeedCannotClaimARequiredCredentialIsConfiguredOrSmuggleItsValue()
    {
        var catalog = new ConnectionModuleCatalog([new("testwire", 1, "Test", new("test", ParameterScope.Connection,
            [new("password", "Password", ParameterType.Credential) { Required = true }]))]);
        Assert.Throws<ArgumentException>(() => catalog.Freeze(1, "testwire", 1, Json("{}")));
        Assert.Throws<ArgumentException>(() => catalog.Freeze(1, "testwire", 1, Json("{\"password\":\"fixture-secret\"}")));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"connectionModule\":null}")]
    [InlineData("{\"connectionModule\":{\"version\":0,\"parameters\":{}}}")]
    [InlineData("{\"connectionModule\":{\"version\":1,\"parameters\":{},\"extra\":true}}")]
    [InlineData("{\"connectionModule\":{\"version\":1,\"parameters\":{\"nested\":{}}}}")]
    [InlineData("{\"connectionModule\":{\"version\":1,\"parameters\":{}},\"credential\":\"fixture-secret\"}")]
    public void HistoricalReadsRejectMalformedEnvelopesWithoutExecutingModules(string configuration) =>
        Assert.Throws<ArgumentException>(() => ModuleConnectionSnapshot.ReadModuleConfiguration(new(1, "testwire", Json(configuration))));

    [Fact]
    public void HostAdmissionUsesInstalledConnectionDefinitionsButHistoricalCanonicalBytesArePreserved()
    {
        var connections = new ConnectionModuleCatalog([Definition()]);
        var factory = new Factory();
        var inputs = new InputModuleCatalog([factory], connections);
        var connection = connections.Freeze(1, "testwire", 1, Json("{\"offset\":9007199254740993}"));
        var source = Source("meter/input", connection);
        var bytes = Document(source);
        var parsed = RuntimePlanDocument.Parse(bytes, inputs: inputs);
        Assert.Equal(parsed.Canonical, RuntimePlanDocument.ParseStored(bytes).Canonical);
        var before = factory.Descriptions;
        Assert.Throws<RuntimePlanDocumentException>(() => RuntimePlanDocument.Parse(bytes, inputs: new([factory])));
        Assert.Equal(before, factory.Descriptions); // missing connection definition fails before module code
        var catalog = RuntimeConnectionCatalog.Read(parsed, null, Guid.Empty.ToString());
        Assert.Equal("testwire", catalog[1].Definition.Kind);
        Assert.Equal(ModuleConnectionSnapshot.ModuleConfigurationIdentity(connection), Assert.Single(catalog[1].Identities).Identity);
        Assert.Equal("meter/input", Assert.Single(catalog[1].Users).Key);
    }

    [Fact]
    public void RepeatedReferencesShareOneIdentityButConflictingParametersAndProtocolsAreRejected()
    {
        var connections = new ConnectionModuleCatalog([Definition()]);
        var inputs = new InputModuleCatalog([new Factory()], connections);
        var one = connections.Freeze(1, "testwire", 1, Json("{\"offset\":1}"));
        var two = connections.Freeze(1, "testwire", 1, Json("{\"offset\":2}"));
        _ = RuntimePlanDocument.Parse(Document(Source("one/read", one), Source("two/read", one)), inputs: inputs);
        Assert.Throws<RuntimePlanDocumentException>(() => RuntimePlanDocument.Parse(Document(Source("one/read", one), Source("two/read", two)), inputs: inputs));
        var http = new RuntimeUnitPlan(RuntimeUnitId.Create("output.http", "sink"), Json("{\"connectionId\":1,\"baseAddress\":\"http://localhost/\",\"path\":\"data\",\"inputs\":[\"one/read\"]}"));
        Assert.Throws<RuntimePlanDocumentException>(() => RuntimePlanDocument.Parse(Document(Source("one/read", one), http), inputs: inputs));
        Assert.Throws<RuntimePlanDocumentException>(() => RuntimePlanDocument.ParseStored(Document(Source("one/read", one), http)));
    }

    [Fact]
    public void CustomConnectionsCountTowardTheOriginalGlobalLimit()
    {
        var connections = new ConnectionModuleCatalog([Definition()]);
        var inputs = new InputModuleCatalog([new Factory()], connections);
        var units = Enumerable.Range(1, 501).Select(id => Source("meter/input-" + id,
            connections.Freeze(id, "testwire", 1, Json("{\"offset\":1}")))).ToArray();
        var accepted = RuntimePlanDocument.Parse(Document(units[..500]), inputs: inputs);
        Assert.Throws<RouteTopologyException>(() => new RouteCompilationLimits(Connections: 499).Check(accepted));
        Assert.Throws<RuntimePlanDocumentException>(() => RuntimePlanDocument.Parse(Document(units), inputs: inputs));
    }

    [Fact]
    public void HistoricalParametersRemainBoundedEvenWithoutTheDeclaration()
    {
        var large = JsonSerializer.SerializeToElement(new { connectionModule = new { version = 1, parameters = new { text = new string('x', 128 * 1024) } } });
        Assert.Throws<ArgumentException>(() => ModuleConnectionSnapshot.ReadModuleConfiguration(new(1, "testwire", large)));
        var many = JsonSerializer.SerializeToElement(new
        {
            connectionModule = new
            {
                version = 1,
                parameters = Enumerable.Range(1, 129).ToDictionary(value => "p" + value, value => value)
            }
        });
        Assert.Throws<ArgumentException>(() => ModuleConnectionSnapshot.ReadModuleConfiguration(new(1, "testwire", many)));
        var unknown = new ConnectionModuleCatalog([Definition()]);
        Assert.Throws<ArgumentException>(() => unknown.Freeze(1, "testwire", 99, Json("{\"offset\":1}")));
    }

    private static RuntimeUnitPlan Source(string key, ModuleConnectionSnapshot connection) => new(RuntimeUnitId.Create("input.testwire", key),
        JsonSerializer.SerializeToElement(new
        {
            inputModule = new
            {
                version = 1,
                parameters = new { source = connection.ConnectionId.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                connections = new { source = new { connectionId = connection.ConnectionId, kind = connection.Kind, configuration = connection.Configuration } }
            }
        }));
    private static byte[] Document(params RuntimeUnitPlan[] units) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        schemaVersion = 1,
        units = units.Select(unit => new { kind = unit.Id.Kind, key = unit.Id.Key, configuration = unit.Configuration })
    });
    private sealed class Factory : IInputModuleFactory
    {
        public int Descriptions { get; private set; }
        public string Kind => "input.testwire";
        public int Version => 1;
        public ParameterSchema Parameters { get; } = new("test", ParameterScope.Input,
            [new("source", "Source", ParameterType.ConnectionReference) { Required = true, ConnectionKinds = ["testwire"] }]);
        public ImmutableArray<InputModuleField> Describe(JsonElement parameters) { Descriptions++; return []; }
        public IRuntimeUnit Build(RuntimeUnitPlan plan, InputUnitContext context) => throw new InvalidOperationException("Must not build during validation");
    }
}
