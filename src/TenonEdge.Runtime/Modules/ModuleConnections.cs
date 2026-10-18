using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Abstractions.Parameters;

namespace TenonEdge.Runtime.Modules;

/// <summary>输入与输出使用同一冻结引用格式；历史结构读取不执行模块。</summary>
internal static class ModuleConnections
{
    internal static void Validate(ParameterSchema schema, JsonElement parameters, IReadOnlyDictionary<string, ModuleConnectionSnapshot> connections)
    {
        var kinds = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in connections)
        {
            var id = pair.Value.ConnectionId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!schema.Parameters.Any(field => field.Name == pair.Key && field.Type == ParameterType.ConnectionReference) ||
                !parameters.TryGetProperty(pair.Key, out var reference) || reference.ValueKind != JsonValueKind.String || reference.GetString() != id ||
                kinds.TryGetValue(id, out var previous) && previous != pair.Value.Kind)
            { throw Invalid(); }
            kinds[id] = pair.Value.Kind;
        }
        foreach (var field in schema.Parameters.Where(field => field.Type == ParameterType.ConnectionReference))
        {
            if (parameters.TryGetProperty(field.Name, out var reference) && reference.ValueKind != JsonValueKind.Null && !connections.ContainsKey(field.Name))
            { throw Invalid(); }
        }
        schema.ThrowIfInvalid(parameters, kinds);
    }

    internal static ImmutableDictionary<string, ModuleConnectionSnapshot> Read(JsonElement envelope)
    {
        if (envelope.ValueKind != JsonValueKind.Object)
        { throw Invalid(); }
        if (!envelope.TryGetProperty("connections", out var value))
        { return ImmutableDictionary<string, ModuleConnectionSnapshot>.Empty; }
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Count() > ParameterSchema.MaximumParameters ||
            Encoding.UTF8.GetByteCount(value.GetRawText()) > ParameterSchema.MaximumConfigurationBytes)
        { throw Invalid(); }
        var result = ImmutableDictionary.CreateBuilder<string, ModuleConnectionSnapshot>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            var item = property.Value;
            if (!BusinessKeys.IsValid(property.Name) || item.ValueKind != JsonValueKind.Object || item.EnumerateObject().Count() != 3 ||
                !item.TryGetProperty("connectionId", out var id) || id.ValueKind != JsonValueKind.Number || !id.TryGetInt64(out var number) || number is < 1 or > 9007199254740991L ||
                !item.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String || !BusinessKeys.IsValid(kind.GetString()) ||
                !item.TryGetProperty("configuration", out var config) || config.ValueKind != JsonValueKind.Object ||
                !envelope.TryGetProperty("parameters", out var parameters) || parameters.ValueKind != JsonValueKind.Object ||
                !parameters.TryGetProperty(property.Name, out var reference) || reference.ValueKind != JsonValueKind.String ||
                reference.GetString() != number.ToString(System.Globalization.CultureInfo.InvariantCulture))
            { throw Invalid(); }
            result.Add(property.Name, new(number, kind.GetString()!, config));
        }
        return result.ToImmutable();
    }

    private static ArgumentException Invalid() => new("模块连接引用无效。");
}
