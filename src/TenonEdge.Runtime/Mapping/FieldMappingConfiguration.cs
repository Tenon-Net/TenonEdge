using System.Text.Json;
using System.Text.Json.Serialization;
using TenonEdge.Abstractions.Messages;

namespace TenonEdge.Runtime.Mapping;

/// <summary>运行快照中的固定规则数组；只做本地编译，不接受表达式或脚本。</summary>
public static class FieldMappingConfiguration
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter<FieldType>(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    public static FieldMapping? Parse(JsonElement configuration)
    {
        if (configuration.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        { return null; }
        var rules = configuration.Deserialize<Rule[]>(Options) ?? throw new ArgumentException("映射规则不能为空。");
        if (rules.Length is < 1 or > 1000)
        { throw new ArgumentException("映射须包含 1—1000 个规则。"); }
        return new FieldMapping(rules.Select(rule =>
        {
            if (rule is null || rule.Type is null)
            { throw new ArgumentException("映射规则须指定目标类型。"); }
            FieldValue? constant = null;
            if (rule.Constant.ValueKind != JsonValueKind.Undefined)
            {
                var result = JsonFieldValueReader.Read(rule.Constant, rule.Type.Value);
                constant = result.Value ?? throw new ArgumentException("固定值不符合声明类型。");
            }
            return new FieldMappingRule(rule.Target, rule.Type.Value, rule.SourceField, rule.SourcePath, constant,
                rule.Unit, rule.Required, rule.Scale, rule.Offset, rule.DecimalPlaces);
        }));
    }

    private sealed record Rule(string Target, FieldType? Type, string? SourceField = null, string? SourcePath = null,
        JsonElement Constant = default, string? Unit = null, bool Required = false, decimal? Scale = null, decimal? Offset = null, int? DecimalPlaces = null);
}
