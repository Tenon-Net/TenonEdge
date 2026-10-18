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
    {
        var snapshots = new Dictionary<string, ModuleConnectionSnapshot>(StringComparer.Ordinal);
        foreach (var (name, id) in references)
        {
            if (available is null || !available.TryGetValue(id, out var connection) || requireEnabled && !connection.Enabled)
            { throw new ArgumentException("模块引用的连接缺失或未启用。"); }
            var validated = ConnectionDraftValidation.Normalize(id, new("module", connection.Key, connection.Name, connection.Kind,
                connection.Enabled, connection.Modbus, connection.Mqtt, connection.Http));
            var kind = validated.Kind.ToString().ToLowerInvariant();
            var configuration = validated.Kind switch
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
            snapshots.Add(name, new(id, kind, configuration));
        }
        return snapshots;
    }
}
