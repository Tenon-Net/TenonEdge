using System.Collections.Immutable;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Runtime.Transforms;

namespace TenonEdge.Runtime.Mapping;

public sealed record FieldMappingPipelineResult(EdgeMessage? Message, int InvalidFields, int MissingFields, int? FailedStage);

/// <summary>旧映射预览/调用入口；执行和预算与通用转换路径共用，不维护第二套转换循环。</summary>
public sealed class FieldMappingPipeline
{
    public const int MaximumStages = TransformPipeline.MaximumStages;
    private readonly TransformPipeline _pipeline;
    public ImmutableArray<MappedFieldSchema> Schema { get; }
    public long WorkingReservationBytes => _pipeline.WorkingReservationBytes;

    public FieldMappingPipeline(IEnumerable<FieldMapping> stages)
    {
        ArgumentNullException.ThrowIfNull(stages);
        _pipeline = new(stages.Select(stage => new FieldMappingTransform(stage ?? throw new ArgumentException("映射阶段不能为空。", nameof(stages)))));
        Schema = _pipeline.Schema.Select(field => new MappedFieldSchema(field.Key, field.Type, field.Unit)).ToImmutableArray();
    }

    public FieldMappingPipelineResult Apply(EdgeMessage source)
    {
        var result = _pipeline.Apply(source);
        return new(result.Message, result.InvalidFields, result.MissingFields, result.FailedStage);
    }
}
