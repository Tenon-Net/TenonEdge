using System.Collections.Immutable;
using System.Text.Json;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Abstractions.Parameters;
using TenonEdge.Abstractions.Plans;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Runtime.Inputs;

namespace TenonEdge.UnitTests.Inputs;

public sealed class InputModuleCatalogTests
{
    [Fact]
    public void DescriptionIsVersionedExactAndDoesNotConstructOrStartAnInput()
    {
        var factory = new Factory();
        var catalog = new InputModuleCatalog([factory, new Factory { Version = 2 }]);
        var plan = Plan("{\"value\":9007199254740993}");
        Assert.Equal(new InputModuleField("value", FieldType.Integer, "unit"), Assert.Single(catalog.Describe(plan)));
        Assert.Equal("9007199254740993", factory.Observed.GetProperty("value").GetRawText());
        Assert.Equal(2, catalog.Modules.Length);
        Assert.Single(catalog.Factories); // Same kind, one runtime factory view, explicit version selection.
        Assert.NotEqual(plan.ConfigurationHash, Plan("{\"value\":9007199254740993}", 2).ConfigurationHash);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"value\":1.5}")]
    [InlineData("{\"value\":-1}")]
    [InlineData("{\"value\":\"1\"}")]
    [InlineData("{\"value\":1,\"password\":\"fixture-secret\"}")]
    [InlineData("{\"value\":{}}")]
    public void InvalidParametersNeverReachDescriptionOrLeakRejectedMaterial(string parameters)
    {
        var factory = new Factory();
        var plan = Plan(parameters);
        var catalog = new InputModuleCatalog([factory]);
        var error = Assert.Throws<ArgumentException>(() => catalog.Describe(plan));
        Assert.Equal(JsonValueKind.Undefined, factory.Observed.ValueKind);
        Assert.DoesNotContain("fixture-secret", error.ToString(), StringComparison.Ordinal);
        Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"inputModule\":null}")]
    [InlineData("{\"inputModule\":{\"version\":0,\"parameters\":{}}}")]
    [InlineData("{\"inputModule\":{\"version\":1,\"parameters\":{},\"extra\":true}}")]
    [InlineData("{\"inputModule\":{\"version\":1,\"parameters\":{}},\"password\":\"fixture-secret\"}")]
    public void ModuleCannotBypassItsEnvelopeOrSmuggleUnvalidatedOuterFields(string json)
    {
        var catalog = new InputModuleCatalog([new Factory()]);
        var unit = new RuntimeUnitPlan(RuntimeUnitId.Create("input.testscalar", "device/input"), Json(json));
        Assert.Throws<ArgumentException>(() => catalog.Validate(unit));
    }

    [Fact]
    public void MissingModuleAndVersionRejectAdmissionButHistoryRetainsTheCanonicalBytes()
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, units = new[] { new { kind = "input.testscalar", key = "device/input", configuration = Plan("{\"value\":1}").Configuration } } });
        var catalog = new InputModuleCatalog([new Factory()]);
        var admitted = RuntimePlanDocument.Parse(bytes, inputs: catalog);
        Assert.Equal(admitted.Canonical, RuntimePlanDocument.ParseStored(bytes).Canonical);
        Assert.Throws<RuntimePlanDocumentException>(() => RuntimePlanDocument.Parse(bytes));
        var unsupported = Plan("{\"value\":1}", 9);
        Assert.Throws<ArgumentException>(() => catalog.Validate(unsupported));
        var raw = new RuntimeUnitPlan(RuntimeUnitId.Create("input.legacy", "device/input"), Json("{\"legacy\":1}"));
        InputModuleCatalog.Empty.Validate(raw); // Existing opaque internal factories are not a public module registration.
    }

    [Theory]
    [InlineData("input.test-scalar")]
    [InlineData("input.UPPER")]
    [InlineData("input..empty")]
    [InlineData("input._invalid")]
    [InlineData("input.")]
    [InlineData("input.9invalid")]
    public void RegistrationUsesTheRuntimeUnitKindRules(string kind)
    {
        var error = Assert.Throws<ArgumentException>(() => new InputModuleCatalog([new Factory { Kind = kind }]));
        Assert.Equal("modules", error.ParamName);
        Assert.Null(error.InnerException);
    }

    [Fact]
    public void RegistrationRejectsCollisionsBuiltinReplacementAndUnsupportedDeclarations()
    {
        Assert.Throws<ArgumentException>(() => new InputModuleCatalog([new Factory(), new Factory()]));
        Assert.Throws<ArgumentException>(() => new InputModuleCatalog([new Factory { Kind = "input.simulator" }]));
        Assert.Throws<ArgumentException>(() => new InputModuleCatalog([new Factory { Version = 0 }]));
        Assert.Throws<ArgumentException>(() => new InputModuleCatalog([new Factory { Kind = "output.example" }]));
        Assert.Throws<ArgumentException>(() => new InputModuleCatalog([new Factory { Parameters = new("connection.test", ParameterScope.Connection,
            [new("password", "凭证", ParameterType.Credential)]) }]));
        Assert.Throws<ArgumentException>(() => new InputModuleCatalog([new Factory(), new Factory { Version = 2, UsesDurableHandoff = true }]));
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("type")]
    [InlineData("count")]
    [InlineData("exception")]
    [InlineData("default")]
    [InlineData("null")]
    [InlineData("key")]
    [InlineData("unit-length")]
    [InlineData("unit-control")]
    public void BrokenModuleMetadataCannotEnterAFrozenPlan(string failure)
    {
        var catalog = new InputModuleCatalog([new Factory { Failure = failure }]);
        var plan = Plan("{\"value\":1}");
        var error = Assert.Throws<ArgumentException>(() => catalog.Describe(plan));
        Assert.DoesNotContain("fixture-secret", error.ToString(), StringComparison.Ordinal);
        Assert.Null(error.InnerException);
    }

    [Fact]
    public void ScalarParameterSizeIsCheckedEvenWhenReadingUninstalledHistory()
    {
        var unit = Plan(JsonSerializer.Serialize(new { value = new string('x', ParameterSchema.MaximumConfigurationBytes) }));
        Assert.Throws<ArgumentException>(() => InputModuleCatalog.ValidateStoredShape(unit));
    }

    [Fact]
    public void FrozenFieldMetadataIsReadableWithoutTheModuleAndCannotLieAboutTheInstalledSchema()
    {
        var catalog = new InputModuleCatalog([new Factory()]);
        var compiled = catalog.WithFrozenFields(Plan("{\"value\":9007199254740993}"));
        InputModuleCatalog.ValidateStoredShape(compiled);
        var field = Assert.Single(InputModuleCatalog.ReadFrozenFields(compiled)!.Value);
        Assert.Equal(new InputModuleField("value", FieldType.Integer, "unit"), field);
        Assert.Equal("9007199254740993", compiled.Configuration.GetProperty("inputModule").GetProperty("parameters").GetProperty("value").GetRawText());
        Assert.Null(InputModuleCatalog.ReadFrozenFields(Plan("{\"value\":1}")));
        Assert.Throws<ArgumentException>(() => new InputModuleCatalog([new Factory { FieldType = FieldType.Number }]).Validate(compiled));
        Assert.Throws<ArgumentException>(() => InputModuleCatalog.Empty.Validate(compiled));
        Assert.Single(catalog.Describe(compiled));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[{\"key\":\"bad key\",\"type\":\"integer\",\"unit\":null}]")]
    [InlineData("[{\"key\":\"value\",\"type\":\"integer\",\"unit\":null,\"extra\":1}]")]
    [InlineData("[{\"key\":\"value\",\"type\":\"integer\",\"unit\":null},{\"key\":\"value\",\"type\":\"number\",\"unit\":null}]")]
    public void InvalidFrozenFieldsAreRejectedEvenWithoutInstalledModules(string fields)
    {
        var unit = new RuntimeUnitPlan(RuntimeUnitId.Create("input.testscalar", "device/input"),
            Json("{\"inputModule\":{\"version\":1,\"parameters\":{\"value\":1},\"fields\":" + fields + "}}"));
        Assert.Throws<ArgumentException>(() => InputModuleCatalog.ValidateStoredShape(unit));
    }

    private static JsonElement Json(string text) => JsonSerializer.Deserialize<JsonElement>(text);
    private static RuntimeUnitPlan Plan(string parameters, int version = 1) => new(RuntimeUnitId.Create("input.testscalar", "device/input"),
        JsonSerializer.SerializeToElement(new { inputModule = new { version, parameters = Json(parameters) } }));

    private sealed class Factory : IInputModuleFactory
    {
        public string Kind { get; init; } = "input.testscalar";
        public int Version { get; init; } = 1;
        public bool UsesDurableHandoff { get; init; }
        public FieldType FieldType { get; init; } = FieldType.Integer;
        public string? Failure { get; init; }
        public JsonElement Observed { get; private set; }
        public ParameterSchema Parameters { get; init; } = new("input.scalar", ParameterScope.Input,
            [new("value", "初值", ParameterType.Number) { Required = true, WholeNumber = true, MinimumJson = "0", MaximumJson = "9223372036854775807" }]);
        public ImmutableArray<InputModuleField> Describe(JsonElement parameters)
        {
            Observed = parameters.Clone();
            return Failure switch
            {
                "default" => default,
                "null" => [null!],
                "key" => [new("bad key", FieldType.Integer, null)],
                "unit-length" => [new("value", FieldType.Integer, new string('x', 65))],
                "unit-control" => [new("value", FieldType.Integer, "unit\n")],
                "exception" => throw new InvalidOperationException("fixture-secret"),
                "duplicate" => [new("value", FieldType.Integer, null), new("value", FieldType.Integer, null)],
                "type" => [new("value", (FieldType)999, null)],
                "count" => Enumerable.Repeat(new InputModuleField("value", FieldType.Integer, null), 1001).ToImmutableArray(),
                _ => [new("value", FieldType, "unit")],
            };
        }
        public IRuntimeUnit Build(RuntimeUnitPlan unit, InputUnitContext context) => throw new InvalidOperationException("Build must not run during metadata validation.");
    }
}
