using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Abstractions.Parameters;
using TenonEdge.Host.Features.Connections;

namespace TenonEdge.Host.Features.Parameters;

/// <summary>输入/输出草稿的显式引用与冻结视图；纯本地转换，不读取数据库或调用模块。</summary>
internal static class ModuleDraftConnections
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) } };

    // Metadata traversal deliberately does not freeze or invoke installed declarations.
    // A missing row remains a visible ID; callers decide whether that is an error.
    internal static HashSet<long> ReadIds(IEnumerable<long> roots, IReadOnlyDictionary<long, ConnectionDraft> available)
    {
        var result = new HashSet<long>();
        var pending = new Stack<long>(roots);
        while (pending.TryPop(out var id))
        {
            if (!result.Add(id))
            { continue; }
            foreach (var child in available.GetValueOrDefault(id)?.Module?.ConnectionIds ?? [])
            { pending.Push(child); }
        }
        return result;
    }

    internal static JsonElement Combine(JsonElement parameters, IReadOnlyDictionary<string, long>? connections)
    {
        if (parameters.ValueKind != JsonValueKind.Object || connections?.Count > ParameterSchema.MaximumParameters ||
            connections?.Any(pair => !BusinessKeys.IsValid(pair.Key) || pair.Value is < 1 or > 9007199254740991L || parameters.TryGetProperty(pair.Key, out _)) == true)
        { throw new ArgumentException("模块标量参数与连接引用无效或重复。"); }
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var property in parameters.EnumerateObject())
            { property.WriteTo(writer); }
            foreach (var pair in connections ?? new Dictionary<string, long>())
            { writer.WriteString(pair.Key, pair.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
            writer.WriteEndObject();
        }
        using var document = JsonDocument.Parse(buffer.ToArray());
        return document.RootElement.Clone();
    }

    internal static Dictionary<string, ModuleConnectionSnapshot> Freeze(IReadOnlyDictionary<string, long> references,
        IReadOnlyDictionary<long, ConnectionDraft>? available, IReadOnlySet<long>? mqttCredentials, bool requireEnabled = false)
        => FreezeCore(references, available, mqttCredentials, requireEnabled, root: false);

    internal static ModuleConnectionSnapshot Freeze(long id, IReadOnlyDictionary<long, ConnectionDraft> available,
        IReadOnlySet<long>? mqttCredentials, bool requireEnabled = false) =>
        FreezeCore(new Dictionary<string, long> { ["connection"] = id }, available, mqttCredentials, requireEnabled, root: true)["connection"];

    private static Dictionary<string, ModuleConnectionSnapshot> FreezeCore(IReadOnlyDictionary<string, long> references,
        IReadOnlyDictionary<long, ConnectionDraft>? available, IReadOnlySet<long>? mqttCredentials, bool requireEnabled, bool root)
    {
        var cached = new Dictionary<long, ModuleConnectionSnapshot>();
        var path = new HashSet<long>();
        // A standalone connection has separate parameter/reference budgets. Do not
        // impose the enclosing input/output reference-map budget on its own parameters.
        try
        { return root ? new(StringComparer.Ordinal) { ["connection"] = Connection(references["connection"]) } : References(references); }
        catch (JsonException) { throw new ArgumentException("模块冻结引用深度或结构无效。"); }

        Dictionary<string, ModuleConnectionSnapshot> References(IReadOnlyDictionary<string, long> values)
        {
            if (values.Count > ParameterSchema.MaximumParameters || values.Any(pair => !BusinessKeys.IsValid(pair.Key) || pair.Value is < 1 or > 9007199254740991L))
            { throw new ArgumentException("模块引用无效或超过数量限制。"); }
            var result = new Dictionary<string, ModuleConnectionSnapshot>(StringComparer.Ordinal);
            var bytes = 2; // JSON braces; every child is already bounded before serialization.
            foreach (var (name, id) in values)
            {
                var snapshot = Connection(id);
                bytes += (result.Count == 0 ? 0 : 1) + Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(name, Json)) + 1 +
                    JsonSerializer.SerializeToUtf8Bytes(snapshot, Json).Length;
                if (bytes > ParameterSchema.MaximumConfigurationBytes)
                { throw new ArgumentException("模块冻结引用（含后代）超过大小限制。"); }
                result.Add(name, snapshot);
            }
            return result;
        }

        ModuleConnectionSnapshot Connection(long id)
        {
            // One edge adds four JSON object levels. This guards the call stack before
            // materialization; the existing JSON MaxDepth=64 still checks final envelopes.
            if (path.Count >= 16 || !path.Add(id))
            { throw new ArgumentException("连接引用循环或深度超限。"); }
            if (cached.TryGetValue(id, out var cachedValue))
            { path.Remove(id); return cachedValue; }
            if (available is null || !available.TryGetValue(id, out var connection) || connection.Id != id || requireEnabled && !connection.Enabled)
            { throw new ArgumentException("模块引用的连接缺失或未启用。"); }
            var validated = ConnectionDraftValidation.Normalize(id, new("module", connection.Key, connection.Name, connection.Kind,
                connection.Enabled, connection.Modbus, connection.Mqtt, connection.Http, connection.Module));
            JsonElement configuration;
            string kind;
            if (validated.Module is { } module)
            {
                kind = module.Kind;
                var envelope = new Dictionary<string, object>
                { ["version"] = module.Version, ["parameters"] = Combine(module.Parameters, module.Connections) };
                if (module.Connections is { Count: > 0 })
                { envelope["connections"] = References(module.Connections); }
                configuration = JsonSerializer.SerializeToElement(new { connectionModule = envelope }, Json);
            }
            else
            {
                kind = validated.Kind.ToString().ToLowerInvariant();
                configuration = validated.Kind switch
                {
                    ConnectionKind.Http => JsonSerializer.SerializeToElement(validated.Http, Json),
                    ConnectionKind.Modbus => JsonSerializer.SerializeToElement(validated.Modbus, Json),
                    ConnectionKind.Mqtt => JsonSerializer.SerializeToElement(new
                    {
                        validated.Mqtt!.Host,
                        validated.Mqtt.Port,
                        validated.Mqtt.Tls,
                        validated.Mqtt.ClientId,
                        validated.Mqtt.TimeoutMs,
                        validated.Mqtt.PingIntervalMs,
                        usePassword = mqttCredentials?.Contains(id) == true,
                    }, Json),
                    _ => throw new ArgumentException("模块连接协议不受支持。"),
                };
            }
            var snapshot = new ModuleConnectionSnapshot(id, kind, configuration);
            cached.Add(id, snapshot);
            path.Remove(id);
            return snapshot;
        }
    }
}
