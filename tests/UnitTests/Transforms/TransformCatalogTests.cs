using System.Collections.Immutable;
using System.Text.Json;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Abstractions.Parameters;
using TenonEdge.Runtime.Transforms;

namespace TenonEdge.UnitTests.Transforms;

public sealed partial class TransformCatalogTests
{
    [Fact]
    public void RegisteredScalarModuleAndBuiltInMappingExecuteInOrderWithoutLosingIntegerPrecision()
    {
        var factory = new OffsetFactory();
        var catalog = new TransformCatalog([factory]);
        using var json = JsonDocument.Parse("""
        {"transformStages":[
          {"kind":"transform.test-offset","version":1,"configuration":{"offset":1}},
          {"kind":"transform.map","version":1,"configuration":{"rules":[{"target":"final","type":"integer","sourceField":"value"}]}}
        ]}
        """);
        var pipeline = catalog.ParsePipeline(json.RootElement)!;
        var source = Message();
        var result = pipeline.Apply(source);
        Assert.Equal("9007199254740994", result.Message!.Values["final"].ToString());
        Assert.Equal("9007199254740993", source.Values["value"].ToString());
        Assert.Equal(source.SourceMessageId, result.Message.SourceMessageId);
        Assert.Equal(new TransformField("final", FieldType.Integer, null), Assert.Single(pipeline.Schema));
        Assert.Equal(1, factory.Builds);
        Assert.Equal(0, result.InvalidFields);
        Assert.Null(result.FailedStage);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"offset\":null}")]
    [InlineData("{\"offset\":1.5}")]
    [InlineData("{\"offset\":101}")]
    [InlineData("{\"offset\":\"1\"}")]
    [InlineData("{\"offset\":1,\"password\":\"do-not-leak\"}")]
    [InlineData("{\"offset\":1,\"offset\":2}")]
    public void ParameterDeclarationIsAppliedBeforeBuildWithoutEchoingRejectedValues(string values)
    {
        var factory = new OffsetFactory();
        var exception = Assert.Throws<ArgumentException>(() => new TransformCatalog([factory]).ParsePipeline(Configuration(values)));
        Assert.Equal(0, factory.Builds);
        Assert.DoesNotContain("do-not-leak", exception.ToString(), StringComparison.Ordinal);
        Assert.Null(exception.InnerException);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("[null]")]
    [InlineData("[{\"kind\":\"transform.test-offset\",\"version\":\"1\",\"configuration\":{}}]")]
    [InlineData("[{\"kind\":\"transform.test-offset\",\"version\":0,\"configuration\":{}}]")]
    [InlineData("[{\"kind\":\"input.test\",\"version\":1,\"configuration\":{}}]")]
    [InlineData("[{\"kind\":\"transform.\",\"version\":1,\"configuration\":{}}]")]
    [InlineData("[{\"kind\":\"transform.test-offset\",\"version\":1,\"configuration\":{},\"secret\":1}]")]
    [InlineData("[{\"kind\":\"transform.test-offset\",\"version\":1,\"configuration\":null}]")]
    public void MalformedDescriptorsAreRejectedEvenWhenReadingHistory(string stages)
    {
        var json = JsonSerializer.Deserialize<JsonElement>("{\"transformStages\":" + stages + "}");
        Assert.Throws<ArgumentException>(() => TransformCatalog.ValidateStoredShape(json));
        Assert.Throws<ArgumentException>(() => TransformCatalog.BuiltIn.ParsePipeline(json));
    }

    [Theory]
    [InlineData("mapping")]
    [InlineData("mappingStages")]
    public void NewAndLegacyFormatsCannotCoexistEvenWithNull(string property)
    {
        var value = JsonSerializer.Deserialize<JsonElement>("{" + $"\"{property}\":null," + "\"transformStages\":[{\"kind\":\"transform.test-offset\",\"version\":1,\"configuration\":{\"offset\":1}}]}");
        Assert.Throws<ArgumentException>(() => new TransformCatalog([new OffsetFactory()]).ParsePipeline(value));
    }

    [Fact]
    public void MissingOrWrongVersionIsNeverSilentlySkippedButCanBeReadStructurally()
    {
        TransformCatalog.ValidateStoredShape(Configuration("{\"offset\":1}"));
        Assert.Throws<ArgumentException>(() => TransformCatalog.BuiltIn.ParsePipeline(Configuration("{\"offset\":1}")));
        Assert.Throws<ArgumentException>(() => new TransformCatalog([new OffsetFactory()]).ParsePipeline(Configuration("{\"offset\":1}", version: 2)));
    }

    [Fact]
    public void DuplicateRegistrationAndInvalidScopeAreRejectedAtConstruction()
    {
        Assert.Throws<ArgumentException>(() => new TransformCatalog([new OffsetFactory(), new OffsetFactory()]));
        Assert.Throws<ArgumentException>(() => new TransformCatalog([new FieldMappingTransformFactory()]));
        Assert.Throws<ArgumentException>(() => new TransformCatalog([new OffsetFactory { Version = 0 }]));
        Assert.Throws<ArgumentException>(() => new TransformCatalog([new OffsetFactory { Parameters = new("test", ParameterScope.Connection,
            [new("secret", "凭证", ParameterType.Credential)]) }]));
        Assert.Throws<ArgumentException>(() => new TransformCatalog([new OffsetFactory { Parameters = new("test", ParameterScope.Transform,
            [new("connection", "连接", ParameterType.ConnectionReference) { ConnectionKinds = ["http"] }]) }]));
    }

    [Fact]
    public void BuildExceptionDoesNotExposeModuleExceptionOrInnerException()
    {
        var factory = new OffsetFactory { Compile = _ => throw new InvalidOperationException("do-not-leak") };
        var error = Assert.Throws<ArgumentException>(() => new TransformCatalog([factory]).ParsePipeline(Configuration("{\"offset\":1}")));
        Assert.DoesNotContain("do-not-leak", error.ToString(), StringComparison.Ordinal);
        Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData(48)]
    [InlineData(49)]
    public void StageLimitAppliesBeforeAnyFactoryBuild(int count)
    {
        var factory = new OffsetFactory();
        var stage = Configuration("{\"offset\":1}").GetProperty("transformStages")[0];
        var json = JsonSerializer.SerializeToElement(new { transformStages = Enumerable.Repeat(stage, count) });
        if (count == 48)
        {
            Assert.NotNull(new TransformCatalog([factory]).ParsePipeline(json));
            Assert.Equal(48, factory.Builds);
        }
        else
        {
            Assert.Throws<ArgumentException>(() => new TransformCatalog([factory]).ParsePipeline(json));
            Assert.Equal(0, factory.Builds);
        }
    }

    internal static JsonElement Configuration(string values, int version = 1) => JsonSerializer.Deserialize<JsonElement>(
        $$"""{"transformStages":[{"kind":"transform.test-offset","version":{{version}},"configuration":{{values}}}]}""");
    internal static EdgeMessage Message() => new(Guid.NewGuid(), "input", "meter", DateTimeOffset.UnixEpoch, TimeSource.Gateway,
        DateTimeOffset.UnixEpoch, new Dictionary<string, FieldValue> { ["value"] = FieldValue.FromInt64(9007199254740993) });

    private sealed class OffsetFactory : IMessageTransformFactory
    {
        public string Kind { get; init; } = "transform.test-offset";
        public int Version { get; init; } = 1;
        public int Builds { get; private set; }
        public Func<long, IMessageTransform> Compile { get; init; } = offset => new Offset(offset);
        public ParameterSchema? Parameters { get; init; } = new("test-offset", ParameterScope.Transform,
            [new("offset", "增量", ParameterType.Number) { Required = true, WholeNumber = true, MinimumJson = "-100", MaximumJson = "100" }]);
        public IMessageTransform Build(JsonElement configuration)
        { Builds++; return Compile(configuration.GetProperty("offset").GetInt64()); }
    }

    private sealed class Offset(long offset) : IMessageTransform
    {
        public ImmutableArray<TransformField> Schema => [new("value", FieldType.Integer, null)];
        public ImmutableArray<string> ReferencedFields => ["value"];
        public long MaximumResultBytes => 4096;
        public long WorkingReservationBytes => 8192;
        public MessageTransformResult Apply(EdgeMessage source)
        {
            if (!source.Values.TryGetValue("value", out var value) || !value.TryGetExactNumber(out var number))
            { return new(null, MissingFields: 1); }
            return new(new(source.SourceMessageId, source.InputId, source.DeviceKey, source.CollectedAt, source.TimeSource, source.ReceivedAt,
                new Dictionary<string, FieldValue> { ["value"] = FieldValue.FromInt64(checked((long)number + offset)) },
                rawJson: source.RawJson, diagnosticAccess: source.DiagnosticAccess, sampleMetadata: source.SampleMetadata));
        }
    }
}
