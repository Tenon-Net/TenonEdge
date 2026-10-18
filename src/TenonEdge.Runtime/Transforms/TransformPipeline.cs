using System.Collections.Immutable;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;

namespace TenonEdge.Runtime.Transforms;

public sealed record TransformPipelineResult(EdgeMessage? Message, int InvalidFields, int MissingFields, int? FailedStage);

/// <summary>顺序执行一条路径，只保留相邻阶段。扩展异常/越界结果仅拒绝本路径，绝不透传扩展异常内容。</summary>
public sealed class TransformPipeline
{
    public const int MaximumStages = 48;
    public const long MaximumStageResultBytes = 4L * 1024 * 1024;
    public const long MaximumStageWorkingBytes = 32L * 1024 * 1024;
    private readonly ImmutableArray<BoundedMessageTransform> _stages;
    public ImmutableArray<TransformField> Schema { get; }
    public long WorkingReservationBytes { get; }

    public TransformPipeline(IEnumerable<IMessageTransform> transforms)
    {
        ArgumentNullException.ThrowIfNull(transforms);
        var builder = ImmutableArray.CreateBuilder<BoundedMessageTransform>();
        long previousResult = 0;
        foreach (var transform in transforms)
        {
            if (transform is null || builder.Count == MaximumStages)
            { throw new ArgumentException("转换阶段须为 1—48 个。", nameof(transforms)); }
            var stage = transform as BoundedMessageTransform ?? new BoundedMessageTransform(transform);
            WorkingReservationBytes = Math.Max(WorkingReservationBytes, previousResult + stage.WorkingReservationBytes);
            previousResult = stage.MaximumResultBytes;
            builder.Add(stage);
            Schema = stage.Schema;
        }
        if (builder.Count == 0)
        { throw new ArgumentException("转换路径至少一个阶段。", nameof(transforms)); }
        _stages = builder.ToImmutable();
    }

    public TransformPipelineResult Apply(EdgeMessage source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var invalid = 0;
        var missing = 0;
        for (var index = 0; index < _stages.Length; index++)
        {
            var result = _stages[index].Apply(source);
            invalid += result.InvalidFields;
            missing += result.MissingFields;
            if (result.Message is not { } next)
            { return new(null, invalid, missing, index); }
            source = next;
        }
        return new(source, invalid, missing, null);
    }

}
