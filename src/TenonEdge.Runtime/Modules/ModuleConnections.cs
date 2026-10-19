using System.Collections.Immutable;
using System.Text.Json;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Parameters;

namespace TenonEdge.Runtime.Modules;

/// <summary>输入与输出使用同一冻结引用格式；历史结构读取不执行模块。</summary>
internal static class ModuleConnections
{
    internal static void Validate(ParameterSchema schema, JsonElement parameters, IReadOnlyDictionary<string, ModuleConnectionSnapshot> connections,
        IReadOnlySet<string>? configuredCredentials = null)
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
        schema.ThrowIfInvalid(parameters, kinds, configuredCredentials);
    }

    internal static ImmutableDictionary<string, ModuleConnectionSnapshot> Read(JsonElement envelope) =>
        ModuleConnectionReferences.Read(envelope);

    private static ArgumentException Invalid() => new("模块连接引用无效。");
}
