using System.Text.Json;
using System.Text.Json.Serialization;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Abstractions.Parameters;
using TenonEdge.Abstractions.Plans;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Devices;
using TenonEdge.Runtime.Inputs;

namespace TenonEdge.Host.Features.Inputs;

/// <summary>非秘密来源参数。字段由已安装模块在事务外描述，不把客户端声称的字段写成可信元数据。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ModuleSourceDraft(string Kind, int Version, JsonElement Parameters,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Dictionary<string, long>? Connections = null)
{
    private static readonly JsonSerializerOptions SnapshotJson = new(JsonSerializerDefaults.Web)
    { Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) } };

    internal IEnumerable<long> ConnectionIds => Connections?.Values ?? Enumerable.Empty<long>();

    internal RuntimeUnitPlan ToUnit(string deviceKey, string inputKey, IReadOnlyDictionary<long, ConnectionDraft>? available = null, IReadOnlySet<long>? mqttCredentials = null)
    {
        var parameters = CombinedParameters();
        var envelope = new Dictionary<string, object?> { ["version"] = Version, ["parameters"] = parameters };
        if (Connections is { Count: > 0 })
        {
            var snapshots = new Dictionary<string, ModuleConnectionSnapshot>(StringComparer.Ordinal);
            foreach (var (name, id) in Connections)
            {
                if (available is null || !available.TryGetValue(id, out var connection))
                { throw new ArgumentException("输入模块引用的连接缺失或协议尚不支持。"); }
                var validated = ConnectionDraftValidation.Normalize(id, new("module", connection.Key, connection.Name, connection.Kind,
                    connection.Enabled, connection.Modbus, connection.Mqtt, connection.Http));
                var kind = validated.Kind.ToString().ToLowerInvariant();
                var configuration = validated.Kind switch
                {
                    ConnectionKind.Http => JsonSerializer.SerializeToElement(validated.Http, SnapshotJson),
                    ConnectionKind.Modbus => JsonSerializer.SerializeToElement(validated.Modbus, SnapshotJson),
                    ConnectionKind.Mqtt => JsonSerializer.SerializeToElement(new
                    {
                        validated.Mqtt!.Host,
                        validated.Mqtt.Port,
                        validated.Mqtt.Tls,
                        validated.Mqtt.ClientId,
                        validated.Mqtt.TimeoutMs,
                        validated.Mqtt.PingIntervalMs,
                        usePassword = mqttCredentials?.Contains(id) == true,
                    }, SnapshotJson),
                    _ => throw new ArgumentException("输入连接协议不受支持。"),
                };
                snapshots.Add(name, new(id, kind, configuration));
            }
            envelope["connections"] = snapshots;
        }
        return new(RuntimeUnitId.Create(Kind, deviceKey + "/" + inputKey), JsonSerializer.SerializeToElement(new { inputModule = envelope }, JsonSerializerOptions.Web));
    }

    // History/export and transaction-local consistency checks do not execute module code.
    // New admission must separately Describe through the installed catalog outside a DB transaction.
    internal ModuleSourceDraft Normalize(DeviceDraft device, string inputKey)
    {
        if (device.Kind != DeviceKind.Module || device.ModuleKind != Kind || Version < 1 || Parameters.ValueKind != JsonValueKind.Object)
        { throw new ArgumentException("输入模块与设备类型不一致。"); }
        // Structural history/export checks do not need installed modules or mutable connections.
        InputModuleCatalog.ValidateStoredShape(new(RuntimeUnitId.Create(Kind, device.Key + "/" + inputKey),
            JsonSerializer.SerializeToElement(new { inputModule = new { version = Version, parameters = CombinedParameters() } })));
        return this with { Parameters = Parameters.Clone(), Connections = Connections?.ToDictionary(StringComparer.Ordinal) };
    }

    private JsonElement CombinedParameters()
    {
        if (Parameters.ValueKind != JsonValueKind.Object || Connections?.Count > ParameterSchema.MaximumParameters ||
            Connections?.Any(pair => !BusinessKeys.IsValid(pair.Key) || pair.Value is < 1 or > 9007199254740991L || Parameters.TryGetProperty(pair.Key, out _)) == true)
        { throw new ArgumentException("输入模块标量参数与连接引用无效或重复。"); }
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var property in Parameters.EnumerateObject())
            { property.WriteTo(writer); }
            foreach (var pair in Connections ?? [])
            { writer.WriteString(pair.Key, pair.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
            writer.WriteEndObject();
        }
        using var document = JsonDocument.Parse(buffer.ToArray());
        return document.RootElement.Clone();
    }
}
