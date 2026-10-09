using System.Collections.Immutable;
using TenonEdge.Abstractions.Messages;

namespace TenonEdge.Runtime.Mapping;

public sealed record FieldMappingPipelineResult(EdgeMessage? Message, int InvalidFields, int MissingFields, int? FailedStage);

/// <summary>一条路径按图顺序执行映射；只保留相邻阶段的消息，不缓存中间报文或跨消息状态。</summary>
public sealed class FieldMappingPipeline
{
    // 首版一条路线最多 50 节点，扣除输入与输出。
    public const int MaximumStages = 48;
    private readonly ImmutableArray<FieldMapping> _stages;
    public ImmutableArray<MappedFieldSchema> Schema => _stages[^1].Schema;
    // 单阶段工作区之外，多阶段还须保留上一阶段结果；不按阶段数累积。
    public long WorkingReservationBytes => FieldMapping.WorkingReservationBytes + (_stages.Length > 1 ? FieldMapping.MaximumResultBytes : 0);

    public FieldMappingPipeline(IEnumerable<FieldMapping> stages)
    {
        ArgumentNullException.ThrowIfNull(stages);
        var builder = ImmutableArray.CreateBuilder<FieldMapping>();
        foreach (var stage in stages)
        {
            ArgumentNullException.ThrowIfNull(stage);
            if (builder.Count == MaximumStages)
            { throw new ArgumentException("单路径最多 48 个映射阶段。", nameof(stages)); }
            builder.Add(stage);
        }
        if (builder.Count == 0)
        { throw new ArgumentException("映射路径至少包含一个阶段。", nameof(stages)); }
        _stages = builder.ToImmutable();
    }

    public FieldMappingPipelineResult Apply(EdgeMessage source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var invalid = 0;
        var missing = 0;
        for (var index = 0; index < _stages.Length; index++)
        {
            var result = _stages[index].Apply(source);
            foreach (var issue in result.Issues)
            {
                if (issue.Reason is null)
                { missing++; }
                else
                { invalid++; }
            }
            if (result.Message is null)
            { return new(null, invalid, missing, index); }
            source = result.Message;
        }
        return new(source, invalid, missing, null);
    }
}
