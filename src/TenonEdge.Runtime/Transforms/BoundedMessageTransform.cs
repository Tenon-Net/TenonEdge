using System.Collections.Immutable;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;

namespace TenonEdge.Runtime.Transforms;

/// <summary>运行与单步预览共用的有界执行检查；工厂返回的原始处理器不直接交给调用方。</summary>
internal sealed class BoundedMessageTransform : IMessageTransform
{
    private readonly IMessageTransform _inner;
    private readonly ImmutableDictionary<string, FieldType> _fields;
    public ImmutableArray<TransformField> Schema { get; }
    public ImmutableArray<string> ReferencedFields { get; }
    public long MaximumResultBytes { get; }
    public long WorkingReservationBytes { get; }

    public BoundedMessageTransform(IMessageTransform inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        Schema = inner.Schema;
        ReferencedFields = inner.ReferencedFields;
        MaximumResultBytes = inner.MaximumResultBytes;
        WorkingReservationBytes = inner.WorkingReservationBytes;
        if (MaximumResultBytes is <= 0 or > TransformPipeline.MaximumStageResultBytes || WorkingReservationBytes < MaximumResultBytes ||
            WorkingReservationBytes > TransformPipeline.MaximumStageWorkingBytes || Schema.IsDefault || Schema.Length > 1000 ||
            ReferencedFields.IsDefault || ReferencedFields.Length > 1000 || ReferencedFields.Any(string.IsNullOrEmpty) ||
            Schema.Any(field => field is null || string.IsNullOrWhiteSpace(field.Key) || field.Key.Length > 64 || !Enum.IsDefined(field.Type)) ||
            Schema.Select(field => field.Key).Distinct(StringComparer.Ordinal).Count() != Schema.Length)
        { throw new ArgumentException("转换字段或工作预算无效。", nameof(inner)); }
        _inner = inner;
        _fields = Schema.ToImmutableDictionary(field => field.Key, field => field.Type, StringComparer.Ordinal);
    }

    public MessageTransformResult Apply(EdgeMessage source)
    {
        ArgumentNullException.ThrowIfNull(source);
        try
        {
            var result = _inner.Apply(source);
            if (result is null || result.InvalidFields is < 0 or > 1000 || result.MissingFields is < 0 or > 1000 ||
                result.Issues.IsDefault || result.Issues.Length > 1000 ||
                result.Issues.Any(issue => issue is null || issue.Field is null || !_fields.ContainsKey(issue.Field) ||
                    issue.Reason is { } reason && !Enum.IsDefined(reason)))
            { throw new ArgumentException("转换诊断无效。"); }
            if (result.Message is not { } next)
            { return result; }
            if (next.SourceMessageId != source.SourceMessageId || next.InputId != source.InputId || next.DeviceKey != source.DeviceKey ||
                next.CollectedAt != source.CollectedAt || next.ReceivedAt != source.ReceivedAt || next.TimeSource != source.TimeSource ||
                !ReferenceEquals(next.RawJson, source.RawJson) || next.DiagnosticAccess != source.DiagnosticAccess ||
                !ReferenceEquals(next.SampleMetadata, source.SampleMetadata) || next.Values.Count + next.Quality.Count > 1000 ||
                next.Values.Any(field => !_fields.TryGetValue(field.Key, out var type) || type != field.Value.Type) ||
                next.Quality.Keys.Any(key => !_fields.ContainsKey(key)) || next.BudgetBytes - (source.RawJson?.BudgetBytes ?? 0) > MaximumResultBytes)
            { throw new ArgumentException("转换结果违反消息或大小约束。"); }
            return result;
        }
        catch (InputMessageSizeException)
        { throw new InputMessageSizeException("转换结果超过本阶段预算，本路径未入队。"); }
        catch (Exception error) when (error is not OutOfMemoryException)
        { throw new ArgumentException("转换模块执行失败或结果无效，本路径未入队。"); }
    }
}
