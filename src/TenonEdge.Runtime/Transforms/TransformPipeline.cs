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
    private readonly ImmutableArray<Stage> _stages;
    public ImmutableArray<TransformField> Schema { get; }
    public long WorkingReservationBytes { get; }

    public TransformPipeline(IEnumerable<IMessageTransform> transforms)
    {
        ArgumentNullException.ThrowIfNull(transforms);
        var builder = ImmutableArray.CreateBuilder<Stage>();
        long previousResult = 0;
        foreach (var transform in transforms)
        {
            if (transform is null || builder.Count == MaximumStages) { throw new ArgumentException("转换阶段须为 1—48 个。", nameof(transforms)); }
            var result = transform.MaximumResultBytes;
            var working = transform.WorkingReservationBytes;
            var schema = transform.Schema;
            var references = transform.ReferencedFields;
            if (result is <= 0 or > MaximumStageResultBytes || working < result || working > MaximumStageWorkingBytes ||
                schema.IsDefault || schema.Length > 1000 || references.IsDefault || references.Length > 1000 ||
                schema.Any(field => field is null || string.IsNullOrWhiteSpace(field.Key) || field.Key.Length > 64 || !Enum.IsDefined(field.Type)) ||
                schema.Select(field => field.Key).Distinct(StringComparer.Ordinal).Count() != schema.Length ||
                references.Any(string.IsNullOrEmpty))
            { throw new ArgumentException("转换字段或工作预算无效。", nameof(transforms)); }
            WorkingReservationBytes = Math.Max(WorkingReservationBytes, previousResult + working);
            previousResult = result;
            builder.Add(new(transform, result, schema.ToImmutableDictionary(field => field.Key, field => field.Type, StringComparer.Ordinal)));
            Schema = schema;
        }
        if (builder.Count == 0) { throw new ArgumentException("转换路径至少一个阶段。", nameof(transforms)); }
        _stages = builder.ToImmutable();
    }

    public TransformPipelineResult Apply(EdgeMessage source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var invalid = 0;
        var missing = 0;
        for (var index = 0; index < _stages.Length; index++)
        {
            try
            {
                var stage = _stages[index];
                var result = stage.Transform.Apply(source);
                if (result is null || result.InvalidFields is < 0 or > 1000 || result.MissingFields is < 0 or > 1000)
                { throw new ArgumentException("转换计数无效。"); }
                invalid += result.InvalidFields;
                missing += result.MissingFields;
                if (result.Message is not { } next) { return new(null, invalid, missing, index); }
                if (next.SourceMessageId != source.SourceMessageId || next.InputId != source.InputId || next.DeviceKey != source.DeviceKey ||
                    next.CollectedAt != source.CollectedAt || next.ReceivedAt != source.ReceivedAt || next.TimeSource != source.TimeSource ||
                    !ReferenceEquals(next.RawJson, source.RawJson) || next.DiagnosticAccess != source.DiagnosticAccess ||
                    !ReferenceEquals(next.SampleMetadata, source.SampleMetadata) ||
                    next.Values.Count + next.Quality.Count > 1000 ||
                    next.Values.Any(field => !stage.Fields.TryGetValue(field.Key, out var type) || type != field.Value.Type) ||
                    next.Quality.Keys.Any(key => !stage.Fields.ContainsKey(key)) || next.BudgetBytes - (source.RawJson?.BudgetBytes ?? 0) > stage.MaximumResultBytes)
                { throw new ArgumentException("转换结果违反消息或大小约束。"); }
                source = next;
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            { throw new ArgumentException("转换模块执行失败或结果无效，本路径未入队。"); }
        }
        return new(source, invalid, missing, null);
    }

    private sealed record Stage(IMessageTransform Transform, long MaximumResultBytes, ImmutableDictionary<string, FieldType> Fields);
}
