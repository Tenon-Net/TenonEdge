using System.Text.Json;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Runtime.Mapping;

namespace TenonEdge.UnitTests.Mapping;

public sealed class FieldMappingPipelineTests
{
    private static EdgeMessage Message() => new(Guid.NewGuid(), "input", "meter", DateTimeOffset.UnixEpoch,
        TimeSource.Gateway, DateTimeOffset.UnixEpoch, new Dictionary<string, FieldValue> { ["value"] = FieldValue.FromInt64(42) });

    [Fact]
    public void EachStageUsesThePreviousResultWithoutChangingTheSourceOrIdentity()
    {
        var source = Message();
        var pipeline = new FieldMappingPipeline([
            new([new("scaled", FieldType.Number, SourceField: "value", Scale: 2m)]),
            new([new("final", FieldType.Number, SourceField: "scaled", Offset: 1m, Unit: "V")])]);
        var result = pipeline.Apply(source);
        Assert.Equal("85", Assert.Single(result.Message!.Values).Value.ToString());
        Assert.Equal(source.SourceMessageId, result.Message.SourceMessageId);
        Assert.Equal(source.InputId, result.Message.InputId);
        Assert.Equal(source.DeviceKey, result.Message.DeviceKey);
        Assert.Equal(source.CollectedAt, result.Message.CollectedAt);
        Assert.Equal(source.ReceivedAt, result.Message.ReceivedAt);
        Assert.Equal(source.TimeSource, result.Message.TimeSource);
        Assert.Equal("42", Assert.Single(source.Values).Value.ToString());
        Assert.Equal(new MappedFieldSchema("final", FieldType.Number, "V"), Assert.Single(pipeline.Schema));
        Assert.Equal(0, result.InvalidFields);
        Assert.Equal(0, result.MissingFields);
        Assert.Null(result.FailedStage);
    }

    [Fact]
    public void RequiredFailureStopsBeforeLaterConstantsCanHideItAndCountsPriorIssues()
    {
        var pipeline = new FieldMappingPipeline([
            new([new("bad", FieldType.Boolean, SourceField: "value"), new("absent", FieldType.Integer, SourceField: "missing")]),
            new([new("must", FieldType.Boolean, SourceField: "bad", Required: true)]),
            new([new("resurrected", FieldType.Integer, Constant: FieldValue.FromInt64(1))])]);
        var result = pipeline.Apply(Message());
        Assert.Null(result.Message);
        Assert.Equal(1, result.FailedStage);
        Assert.Equal(2, result.InvalidFields);
        Assert.Equal(1, result.MissingFields);
    }

    [Fact]
    public void RawJsonRemainsAvailableToLaterStagesAndMissingValuesDoNotLeakBetweenCalls()
    {
        using var json = JsonDocument.Parse("""{"n":9223372036854775807}""");
        var message = Message();
        var source = new EdgeMessage(message.SourceMessageId, message.InputId, message.DeviceKey, message.CollectedAt,
            message.TimeSource, message.ReceivedAt, message.Values, rawJson: RawJsonPayload.CopyFrom(json.RootElement));
        var pipeline = new FieldMappingPipeline([
            new([new("discarded", FieldType.Integer, SourceField: "value")]),
            new([new("exact", FieldType.Integer, SourcePath: "/n")])]);
        Assert.Equal("9223372036854775807", pipeline.Apply(source).Message!.Values["exact"].ToString());
        var next = pipeline.Apply(message);
        Assert.Empty(next.Message!.Values);
        Assert.Equal(1, next.MissingFields);
    }

    [Theory]
    [InlineData("{\"mappingStages\":[]}")]
    [InlineData("{\"mappingStages\":null}")]
    [InlineData("{\"mappingStages\":[null]}")]
    [InlineData("{\"mappingStages\":[[]]}")]
    [InlineData("{\"mapping\":null,\"mappingStages\":[]}")]
    public void InvalidOrAmbiguousStageConfigurationCannotBeSilentlyIgnored(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Throws<ArgumentException>(() => FieldMappingConfiguration.ParsePipeline(document.RootElement));
    }

    [Theory]
    [InlineData(1, 8)]
    [InlineData(2, 12)]
    [InlineData(48, 12)]
    public void WorkingBudgetCoversPreviousResultButDoesNotGrowWithTheWholeChain(int stages, int mebibytes)
    {
        var mapping = new FieldMapping([new("value", FieldType.Integer, SourceField: "value")]);
        var pipeline = new FieldMappingPipeline(Enumerable.Repeat(mapping, stages));
        Assert.Equal(mebibytes * 1024L * 1024, pipeline.WorkingReservationBytes);
    }

    [Theory]
    [InlineData(48)]
    [InlineData(49)]
    public void SerializedStageLimitMatchesTheRuntimeLimit(int count)
    {
        var rules = JsonSerializer.Deserialize<JsonElement>("""[{"target":"value","type":"integer","sourceField":"value"}]""");
        var configuration = JsonSerializer.SerializeToElement(new { mappingStages = Enumerable.Repeat(rules, count).ToArray() });
        if (count == 48)
        { Assert.NotNull(FieldMappingConfiguration.ParsePipeline(configuration)); }
        else
        { Assert.Throws<ArgumentException>(() => FieldMappingConfiguration.ParsePipeline(configuration)); }
    }

    [Fact]
    public void StageLimitsApplyBeforeExecutingAndLegacyMappingStillWorks()
    {
        var stage = new FieldMapping([new("value", FieldType.Integer, SourceField: "value")]);
        Assert.NotNull(new FieldMappingPipeline(Enumerable.Repeat(stage, 48)).Apply(Message()).Message);
        Assert.Throws<ArgumentException>(() => new FieldMappingPipeline(Enumerable.Repeat(stage, 49)));
        Assert.Throws<ArgumentException>(() => new FieldMappingPipeline([]));
        using var json = JsonDocument.Parse("""{"mapping":[{"target":"legacy","type":"integer","sourceField":"value"}]}""");
        Assert.Equal("42", FieldMappingConfiguration.ParsePipeline(json.RootElement)!.Apply(Message()).Message!.Values["legacy"].ToString());
        using var empty = JsonDocument.Parse("{}");
        Assert.Null(FieldMappingConfiguration.ParsePipeline(empty.RootElement));
    }
}
