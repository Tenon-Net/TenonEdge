using System.Collections.Immutable;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Runtime.Transforms;

namespace TenonEdge.UnitTests.Transforms;

public sealed class TransformPipelineTests
{
    [Theory]
    [InlineData(0, 1024)]
    [InlineData(-1, 1024)]
    [InlineData(4194305, 8388608)]
    [InlineData(1024, 1023)]
    [InlineData(1024, 33554433)]
    public void InvalidBudgetIsRejectedBeforeApply(long result, long work) =>
        Assert.Throws<ArgumentException>(() => new TransformPipeline([new TestTransform { MaximumResultBytes = result, WorkingReservationBytes = work }]));

    [Fact]
    public void BudgetUsesLargestAdjacentPairRatherThanSumOrNumberOfPaths()
    {
        var a = new TestTransform { MaximumResultBytes = 4096, WorkingReservationBytes = 8192 };
        var b = new TestTransform { MaximumResultBytes = 1024, WorkingReservationBytes = 8192 };
        Assert.Equal(12288, new TransformPipeline([a, b, a]).WorkingReservationBytes);
        Assert.Equal(12288, new TransformPipeline(Enumerable.Repeat(a, 48)).WorkingReservationBytes);
    }

    [Fact]
    public void FailureStopsTheChainAndCountsOnlyExecutedStages()
    {
        var called = false;
        var pipeline = new TransformPipeline([
            new TestTransform { Run = source => new(source, 1, 2) },
            new TestTransform { Run = _ => new(null, 3, 4) },
            new TestTransform { Run = source => { called = true; return new(source); } }]);
        var result = pipeline.Apply(TransformCatalogTests.Message());
        Assert.Null(result.Message);
        Assert.Equal(1, result.FailedStage);
        Assert.Equal(4, result.InvalidFields);
        Assert.Equal(6, result.MissingFields);
        Assert.False(called);
    }

    [Theory]
    [InlineData("id")]
    [InlineData("device")]
    [InlineData("input")]
    [InlineData("time")]
    [InlineData("raw")]
    [InlineData("diagnostics")]
    [InlineData("type")]
    [InlineData("field")]
    [InlineData("quality")]
    [InlineData("size")]
    [InlineData("counts")]
    [InlineData("exception")]
    public void InvalidModuleResultsAndExceptionsAreSafePathFailures(string failure)
    {
        MessageTransformResult Run(EdgeMessage source)
        {
            if (failure == "exception") { throw new InvalidOperationException("do-not-leak"); }
            if (failure == "counts") { return new(source, -1); }
            var values = source.Values;
            if (failure == "type") { values = values.SetItem("value", FieldValue.FromBoolean(true)); }
            if (failure == "field") { values = values.Add("undeclared", FieldValue.FromInt64(1)); }
            if (failure == "size") { values = values.SetItem("value", FieldValue.FromString(new string('x', 4096))); }
            return new(new(
                failure == "id" ? Guid.NewGuid() : source.SourceMessageId,
                failure == "input" ? "different" : source.InputId,
                failure == "device" ? "different" : source.DeviceKey,
                failure == "time" ? source.CollectedAt.AddSeconds(1) : source.CollectedAt,
                source.TimeSource,
                failure == "time" ? source.ReceivedAt.AddSeconds(1) : source.ReceivedAt,
                values,
                failure == "quality" ? new Dictionary<string, QualityCode> { ["unknown"] = QualityCode.ConversionFailed } : source.Quality,
                failure == "raw" ? RawJsonPayload.CopyFrom(System.Text.Json.JsonSerializer.SerializeToElement(new { secret = 1 })) : source.RawJson,
                failure == "diagnostics" ? DiagnosticSampleAccess.Safe : source.DiagnosticAccess,
                source.SampleMetadata));
        }
        var transform = new TestTransform { Run = Run };
        if (failure == "size") { transform = transform with { Schema = [new("value", FieldType.String, null)] }; }
        var exception = Assert.Throws<ArgumentException>(() => new TransformPipeline([transform]).Apply(TransformCatalogTests.Message()));
        Assert.DoesNotContain("do-not-leak", exception.ToString(), StringComparison.Ordinal);
        Assert.Null(exception.InnerException);
    }

    private sealed record TestTransform : IMessageTransform
    {
        public ImmutableArray<TransformField> Schema { get; init; } = [new("value", FieldType.Integer, null)];
        public ImmutableArray<string> ReferencedFields => ["value"];
        public long MaximumResultBytes { get; init; } = 4096;
        public long WorkingReservationBytes { get; init; } = 8192;
        public Func<EdgeMessage, MessageTransformResult> Run { get; init; } = source => new(source);
        public MessageTransformResult Apply(EdgeMessage source) => Run(source);
    }
}
