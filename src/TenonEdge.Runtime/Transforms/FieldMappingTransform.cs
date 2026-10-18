using System.Collections.Immutable;
using System.Text.Json;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Abstractions.Parameters;
using TenonEdge.Runtime.Mapping;

namespace TenonEdge.Runtime.Transforms;

internal sealed class FieldMappingTransform(FieldMapping mapping) : IMessageTransform
{
    public ImmutableArray<TransformField> Schema { get; } = mapping.Schema.Select(field => new TransformField(field.Key, field.Type, field.Unit)).ToImmutableArray();
    public ImmutableArray<string> ReferencedFields => mapping.ReferencedFields;
    public long MaximumResultBytes => FieldMapping.MaximumResultBytes;
    public long WorkingReservationBytes => FieldMapping.WorkingReservationBytes;
    public MessageTransformResult Apply(EdgeMessage source)
    {
        var result = mapping.Apply(source);
        return new(result.Message, result.Issues.Count(issue => issue.Reason is not null), result.Issues.Count(issue => issue.Reason is null))
        { Issues = result.Issues.Select(issue => new TransformIssue(issue.Field, issue.Reason, issue.Required)).ToImmutableArray() };
    }
}

public sealed class FieldMappingTransformFactory : IMessageTransformFactory
{
    public string Kind => "transform.map";
    public int Version => 1;
    public string DisplayName => "字段映射";
    public ParameterSchema? Parameters => null;
    public IMessageTransform Build(JsonElement configuration)
    {
        if (configuration.ValueKind != JsonValueKind.Object || configuration.EnumerateObject().Count() != 1 ||
            !configuration.TryGetProperty("rules", out var rules))
        { throw new ArgumentException("映射转换只接受 rules。", nameof(configuration)); }
        return new FieldMappingTransform(FieldMappingConfiguration.Parse(rules) ?? throw new ArgumentException("映射规则不能为空。", nameof(configuration)));
    }
}
