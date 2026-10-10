using System.Collections.Immutable;
using System.Text.Json;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;

namespace TenonEdge.Runtime.Mapping;

public sealed record FieldMappingRule(string Target, FieldType Type, string? SourceField = null, string? SourcePath = null,
    FieldValue? Constant = null, string? Unit = null, bool Required = false,
    decimal? Scale = null, decimal? Offset = null, int? DecimalPlaces = null);
public sealed record MappedFieldSchema(string Key, FieldType Type, string? Unit);
public sealed record FieldMappingIssue(string Field, QualityCode? Reason, bool Required);
public sealed record FieldMappingResult(EdgeMessage? Message, ImmutableArray<FieldMappingIssue> Issues);

/// <summary>单路径映射；必填失败不交出半成品。仅使用本条数据，每次返回独立不可变结果与输出 schema。</summary>
public sealed class FieldMapping
{
    public const long MaximumResultBytes = 4 * 1024 * 1024;
    public const long WorkingReservationBytes = 8 * 1024 * 1024;
    private readonly ImmutableArray<CompiledRule> _rules;
    public ImmutableArray<MappedFieldSchema> Schema { get; }
    public ImmutableArray<string> ReferencedFields { get; }

    public FieldMapping(IEnumerable<FieldMappingRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var compiled = ImmutableArray.CreateBuilder<CompiledRule>();
        var schema = ImmutableArray.CreateBuilder<MappedFieldSchema>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in rules)
        {
            ArgumentNullException.ThrowIfNull(rule);
            if (compiled.Count >= 1000 || string.IsNullOrWhiteSpace(rule.Target) || rule.Target.Length > 64 || !names.Add(rule.Target))
            { throw new ArgumentException("映射目标字段不能为空或重名。", nameof(rules)); }
            var sourceCount = (rule.SourceField is not null ? 1 : 0) + (rule.SourcePath is not null ? 1 : 0) + (rule.Constant is not null ? 1 : 0);
            if (sourceCount != 1 || rule.SourceField is { Length: 0 })
            { throw new ArgumentException("每个映射须且只能指定来源字段、JSON 位置或固定值之一。", nameof(rules)); }
            compiled.Add(new(rule, rule.SourcePath is null ? null : new JsonFieldPointer(rule.SourcePath),
                new FieldValueConversion(rule.Type, rule.Scale, rule.Offset, rule.DecimalPlaces)));
            schema.Add(new(rule.Target, rule.Type, rule.Unit));
        }
        _rules = compiled.ToImmutable();
        Schema = schema.ToImmutable();
        ReferencedFields = _rules.Select(rule => rule.Rule.SourceField).OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToImmutableArray();
    }

    public FieldMappingResult Apply(EdgeMessage source, JsonElement payload = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (payload.ValueKind == JsonValueKind.Undefined && source.RawJson is { } raw)
        { payload = raw.Root; }
        var values = new Dictionary<string, FieldValue>(StringComparer.Ordinal);
        var quality = new Dictionary<string, QualityCode>(StringComparer.Ordinal);
        var issues = ImmutableArray.CreateBuilder<FieldMappingIssue>();
        var failed = false;
        long resultBytes = 0;
        foreach (var compiled in _rules)
        {
            var rule = compiled.Rule;
            FieldConversionResult? result = null;
            if (rule.Constant is { } constant)
            { result = compiled.Conversion.Convert(constant); }
            else if (rule.SourceField is { } field)
            {
                if (source.Quality.TryGetValue(field, out var bad))
                { result = new(null, bad); }
                else if (source.Values.TryGetValue(field, out var value))
                { result = compiled.Conversion.Convert(value); }
            }
            else if (compiled.Location!.TryResolve(payload, out var value))
            { result = compiled.Conversion.Convert(value); }

            resultBytes += 256L + 2L * rule.Target.Length + (result?.Value?.BudgetBytes ?? 0);
            if (resultBytes > MaximumResultBytes)
            { throw new InputMessageSizeException("映射结果超过 4 MiB。"); }
            if (result?.Value is { } converted)
            { values.Add(rule.Target, converted); continue; }
            var error = result?.Error;
            // 不存在的可选字段是“未携带”；已有 bad 或转换错误必须保留原因，不能混为缺失。
            if (error is { } reason)
            { quality.Add(rule.Target, reason); }
            issues.Add(new(rule.Target, error, rule.Required));
            failed |= rule.Required;
        }
        if (failed)
        { return new(null, issues.ToImmutable()); }
        return new(new EdgeMessage(source.SourceMessageId, source.InputId, source.DeviceKey, source.CollectedAt,
            source.TimeSource, source.ReceivedAt, values, quality, source.RawJson, source.DiagnosticAccess, source.SampleMetadata), issues.ToImmutable());
    }

    private sealed record CompiledRule(FieldMappingRule Rule, JsonFieldPointer? Location, FieldValueConversion Conversion);
}
