using System.Globalization;
using System.Text.Json;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Parameters;
using TenonEdge.Abstractions.Plans;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Runtime.Connections;

namespace TenonEdge.UnitTests.Connections;

public sealed class NestedConnectionReferenceTests
{
    private static JsonElement Json(string text) => JsonSerializer.Deserialize<JsonElement>(text);
    private static ModuleConnectionSnapshot Http(long id, string address = "http://localhost/") =>
        new(id, "http", JsonSerializer.SerializeToElement(new { baseAddress = address }));
    private static ModuleConnectionSnapshot Parent(long id, Dictionary<string, ModuleConnectionSnapshot> references) => new(id, "wrapper",
        JsonSerializer.SerializeToElement(new
        {
            connectionModule = new
            {
                version = 1,
                parameters = references.ToDictionary(pair => pair.Key, pair => pair.Value.ConnectionId.ToString(CultureInfo.InvariantCulture)),
                connections = references
            }
        }, JsonSerializerOptions.Web));
    private static ConnectionModuleDefinition Wrapper(params string[] kinds) => new("wrapper", 1, "Wrapper",
        new("connection.wrapper", ParameterScope.Connection,
            [new("primary", "Primary", ParameterType.ConnectionReference) { Required = true, ConnectionKinds = [.. kinds] }]));
    private static RuntimeUnitPlan Source(ModuleConnectionSnapshot connection) => new(RuntimeUnitId.Create("input.wrapper", "meter/source" + connection.ConnectionId),
        JsonSerializer.SerializeToElement(new
        {
            inputModule = new { version = 1, parameters = new { source = connection.ConnectionId.ToString(CultureInfo.InvariantCulture) }, connections = new { source = connection } }
        }, JsonSerializerOptions.Web));
    private static byte[] Document(params RuntimeUnitPlan[] units) => JsonSerializer.SerializeToUtf8Bytes(new
    { schemaVersion = 1, units = units.Select(unit => new { kind = unit.Id.Kind, key = unit.Id.Key, configuration = unit.Configuration }) });

    [Fact]
    public void AliasesShareOneConnectionButChangesToDescendantsChangeTheSharedIdentity()
    {
        var child = Http(2);
        var parent = Parent(1, new() { ["primary"] = child, ["backup"] = child });
        var plan = RuntimePlanDocument.ParseStored(Document(Source(parent)));
        Assert.Equal([1L, 2L], RuntimeConnectionReferences.ReadIds(plan.Units[0]).Order());
        var changed = Parent(1, new() { ["primary"] = Http(2, "http://other/"), ["backup"] = Http(2, "http://other/") });
        Assert.NotEqual(ModuleConnectionSnapshot.ModuleConfigurationIdentity(parent), ModuleConnectionSnapshot.ModuleConfigurationIdentity(changed));
        Assert.Equal(plan.Canonical, RuntimePlanDocument.ParseStored(plan.Canonical).Canonical);
    }

    [Fact]
    public void CyclesAndConflictingAliasesAreRejectedWithoutReturningPartialReferences()
    {
        var cycle = Parent(1, new() { ["primary"] = Parent(2, new() { ["primary"] = Http(1) }) });
        Assert.Throws<ArgumentException>(() => ModuleConnectionSnapshot.ReadModuleConfiguration(cycle));
        var conflict = Parent(1, new() { ["primary"] = Http(2), ["backup"] = Http(2, "http://other/") });
        Assert.Throws<ArgumentException>(() => ModuleConnectionSnapshot.ReadModuleConfiguration(conflict));
        var protocolConflict = Parent(1, new() { ["primary"] = Http(2), ["backup"] = new(2, "mqtt", Json("{\"host\":\"localhost\"}")) });
        Assert.Throws<ArgumentException>(() => ModuleConnectionSnapshot.ReadModuleConfiguration(protocolConflict));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"primary\":null}")]
    [InlineData("{\"primary\":{\"connectionId\":3,\"kind\":\"http\",\"configuration\":{}}}")]
    [InlineData("{\"primary\":{\"connectionId\":2,\"kind\":\"http\",\"configuration\":{},\"secret\":\"fixture-secret\"}}")]
    [InlineData("{\"primary\":{\"connectionId\":2,\"kind\":\"wrapper\",\"configuration\":{}}}")]
    public void MalformedDescendantsAreRejectedWithoutEchoingValues(string references)
    {
        var parent = new ModuleConnectionSnapshot(1, "wrapper", Json("{\"connectionModule\":{\"version\":1,\"parameters\":{\"primary\":\"2\"},\"connections\":" + references + "}}"));
        var error = Assert.Throws<ArgumentException>(() => ModuleConnectionSnapshot.ReadModuleConfiguration(parent));
        Assert.DoesNotContain("fixture-secret", error.ToString(), StringComparison.Ordinal);
        Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData("{\"baseAddress\":\"http://localhost/\",\"password\":\"fixture-secret\"}")]
    [InlineData("{\"baseAddress\":\"http://user:fixture-secret@localhost/\"}")]
    [InlineData("{\"baseAddress\":\"http://localhost/\",\"timeoutMs\":1}")]
    public void HistoricalProtocolValidationAlsoCoversDescendants(string configuration)
    {
        var parent = Parent(1, new() { ["primary"] = new(2, "http", Json(configuration)) });
        var error = Assert.Throws<RuntimePlanDocumentException>(() => RuntimePlanDocument.ParseStored(Document(Source(parent))));
        Assert.DoesNotContain("fixture-secret", error.ToString(), StringComparison.Ordinal);
        Assert.Null(error.InnerException);
    }

    [Fact]
    public void ReferenceSubtreesRemainBoundedByTheExistingCountAndByteBudgets()
    {
        var many = Parent(1, Enumerable.Range(2, 129).ToDictionary(id => "p" + id, id => Http(id)));
        Assert.Throws<ArgumentException>(() => ModuleConnectionSnapshot.ReadModuleConfiguration(many));
        var large = Parent(1, new() { ["primary"] = new(2, "http", JsonSerializer.SerializeToElement(new { text = new string('x', ParameterSchema.MaximumConfigurationBytes) })) });
        Assert.Throws<ArgumentException>(() => ModuleConnectionSnapshot.ReadModuleConfiguration(large));
    }

    [Theory]
    [InlineData(99, true)]
    [InlineData(100, false)]
    public void DescendantsCannotBypassTheFiveHundredConnectionPlanLimit(int childCount, bool accepted)
    {
        var units = Enumerable.Range(0, 5).Select(index => Source(Parent(1001 + index,
            Enumerable.Range(index * 100 + 1, childCount).ToDictionary(id => "p" + id, id => Http(id))))).ToArray();
        if (accepted)
        { Assert.Equal(500, RuntimePlanDocument.ParseStored(Document(units)).Units.SelectMany(RuntimeConnectionReferences.ReadIds).Distinct().Count()); }
        else
        { Assert.Throws<RuntimePlanDocumentException>(() => RuntimePlanDocument.ParseStored(Document(units))); }
    }

    [Fact]
    public void FrozenReferenceMustMatchItsDeclaredFieldAndProtocol()
    {
        var parent = Parent(1, new() { ["primary"] = Http(2) });
        new ConnectionModuleCatalog([Wrapper("http")]).Validate(parent);
        Assert.Throws<ArgumentException>(() => new ConnectionModuleCatalog([Wrapper("mqtt")]).Validate(parent));
        var scalar = new ConnectionModuleDefinition("wrapper", 1, "Wrapper", new("wrapper", ParameterScope.Connection,
            [new("primary", "Primary", ParameterType.Text) { Required = true, MaximumLength = 64 }]));
        Assert.Throws<ArgumentException>(() => new ConnectionModuleCatalog([scalar]).Validate(parent));
        Assert.Throws<ArgumentException>(() => new ConnectionModuleCatalog([Wrapper("http")]).Freeze(1, "wrapper", 1, Json("{\"primary\":\"2\"}")));
    }

    [Fact]
    public void EveryCustomDescendantRequiresItsExactInstalledDefinition()
    {
        var leaf = new ModuleConnectionSnapshot(2, "leaf", Json("{\"connectionModule\":{\"version\":1,\"parameters\":{}}}"));
        var parent = Parent(1, new() { ["primary"] = leaf });
        _ = ModuleConnectionSnapshot.ReadModuleConfiguration(parent);
        Assert.Throws<ArgumentException>(() => new ConnectionModuleCatalog([Wrapper("leaf")]).Validate(parent));
        var definition = new ConnectionModuleDefinition("leaf", 1, "Leaf", new("leaf", ParameterScope.Connection, [new("offset", "Offset", ParameterType.Number)]));
        new ConnectionModuleCatalog([Wrapper("leaf"), definition]).Validate(parent);
        var wrongVersion = new ConnectionModuleDefinition("leaf", 2, "Leaf", definition.Parameters);
        Assert.Throws<ArgumentException>(() => new ConnectionModuleCatalog([Wrapper("leaf"), wrongVersion]).Validate(parent));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DescendantCredentialsAreCheckedAtUseAndUnusableOptionalMaterialCannotBecomeAnonymous(bool required)
    {
        var definition = new ConnectionModuleDefinition("authleaf", 1, "Leaf", new("leaf", ParameterScope.Connection,
            [new("host", "Host", ParameterType.Text) { Required = true, MaximumLength = 255 }, new("auth", "Auth", ParameterType.Credential) { Required = required }]), ["host"]);
        var catalog = new ConnectionModuleCatalog([Wrapper("authleaf"), definition]);
        var leaf = new ModuleConnectionSnapshot(2, "authleaf", Json("{\"connectionModule\":{\"version\":1,\"parameters\":{\"host\":\"localhost\"}}}"));
        var parent = Parent(1, new() { ["primary"] = leaf });
        catalog.Validate(parent); // Editing/freeze never claims secret material exists.
        if (required)
        { Assert.Throws<ArgumentException>(() => catalog.ValidateForUse(parent, null)); }
        else
        { catalog.ValidateForUse(parent, null); }
        var identity = new ModuleCredentialIdentity("authleaf", 1,
            definition.RecipientIdentity(ModuleConnectionSnapshot.ReadModuleConfiguration(leaf).Parameters), "user", "marker", "v1");
        Assert.Throws<ArgumentException>(() => catalog.ValidateForUse(parent, new Resolver(identity, null)));
        var resolver = new Resolver(identity, new(identity, new Dictionary<string, string> { ["auth"] = "fixture-secret" }));
        catalog.ValidateForUse(parent, resolver);
        Assert.Equal(2, resolver.Resolved!.ConnectionId);
        Assert.Equal(leaf.Configuration.GetRawText(), resolver.Resolved.Configuration.GetRawText());
        Assert.DoesNotContain("fixture-secret", parent.Configuration.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public void ReferenceFreeCanonicalBytesDoNotChange()
    {
        var definition = new ConnectionModuleDefinition("leaf", 1, "Leaf", new("leaf", ParameterScope.Connection,
            [new("value", "Value", ParameterType.Number) { Required = true, WholeNumber = true }]));
        var leaf = new ConnectionModuleCatalog([definition]).Freeze(1, "leaf", 1, Json("{\"value\":9007199254740993}"));
        Assert.Equal("{\"connectionModule\":{\"parameters\":{\"value\":9007199254740993},\"version\":1}}", leaf.Configuration.GetRawText());
    }

    private sealed class Resolver(ModuleCredentialIdentity identity, ModuleCredential? value) : IModuleCredentialResolver
    {
        internal ModuleConnectionSnapshot? Resolved { get; private set; }
        public ModuleCredentialIdentity? DescribeModule(long connectionId) => connectionId == 2 ? identity : null;
        public ModuleCredential? ResolveModule(ModuleConnectionSnapshot connection)
        { Resolved = connection; return connection.ConnectionId == 2 ? value : null; }
    }
}
