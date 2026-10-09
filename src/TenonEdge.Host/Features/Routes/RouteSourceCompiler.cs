using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Abstractions.Plans;
using TenonEdge.Adapters.Http;
using TenonEdge.Adapters.Modbus;
using TenonEdge.Adapters.Mqtt;
using TenonEdge.Adapters.Simulator;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Devices;
using TenonEdge.Host.Features.Inputs;
using TenonEdge.Runtime.Mapping;

namespace TenonEdge.Host.Features.Routes;

internal sealed record CompiledRouteSource(RouteTopologySource Source, RuntimeUnitPlan Unit);

/// <summary>把已保存设备/来源/连接转成非秘密配置；只解析与描述字段，不调用 Build/网络/运行时。</summary>
internal static class RouteSourceCompiler
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) } };

    internal static readonly JsonSerializerOptions SettingsJson = new(Json) { IgnoreReadOnlyProperties = true };

    public static CompiledRouteSource Compile(InputDraft input, DeviceDraft device, ConnectionDraft? connection, bool mqttRequiresPassword)
    {
        if (input.DeviceId != device.Id || input.Kind != device.Kind)
        { throw new ArgumentException("来源与设备不匹配。"); }
        _ = DeviceDraftValidation.Normalize(device.Id, new("compile", device.Key, device.Name, device.Kind, device.Group,
            device.ConnectionId, device.UnitId, device.AddressNotation, device.Enabled), connection);
        input = InputDraftValidation.Normalize(input.Id, device, new("compile", input.Key, input.Name, input.Enabled,
            input.Modbus, input.Mqtt, input.Http, input.Simulator), connection);
        var id = RuntimeUnitId.Create("input." + input.Kind.ToString().ToLowerInvariant(), device.Key + "/" + input.Key);
        JsonElement configuration;
        ImmutableArray<MappedFieldSchema> fields;
        switch (input.Kind)
        {
            case DeviceKind.Modbus:
                var transport = connection!.Modbus!;
                var modbus = new ModbusInputSettings
                {
                    ConnectionId = connection.Id,
                    Host = transport.Host,
                    Port = transport.Port,
                    FrameFormat = transport.Format == ModbusFrameFormat.Tcp ? "tcp" : "rtu-over-tcp",
                    ConnectTimeoutMs = transport.ConnectTimeoutMs,
                    ReadTimeoutMs = transport.ReadTimeoutMs,
                    RequestIntervalMs = transport.RequestIntervalMs,
                    RtuSilenceMs = transport.RtuSilenceMs,
                    UnitId = checked((byte)device.UnitId!.Value),
                    PeriodMs = input.Modbus!.PeriodMs,
                    Points = input.Modbus.Points.ToImmutableArray(),
                };
                configuration = JsonSerializer.SerializeToElement(modbus, SettingsJson);
                _ = ModbusInputSettings.Parse(configuration);
                fields = input.Modbus.Points.Where(point => point.Enabled).Select(point => new MappedFieldSchema(point.Key,
                    point.Type is "bool" or "bit" ? FieldType.Boolean : point.Type is "float32" or "float64" ? FieldType.Number : FieldType.Integer, point.Unit)).ToImmutableArray();
                break;
            case DeviceKind.Mqtt:
                var mqtt = connection!.Mqtt!;
                var source = input.Mqtt!;
                configuration = JsonSerializer.SerializeToElement(new MqttInputSettings
                {
                    ConnectionId = connection.Id,
                    Host = mqtt.Host,
                    Port = mqtt.Port,
                    Tls = mqtt.Tls,
                    ClientId = mqtt.ClientId,
                    TimeoutMs = mqtt.TimeoutMs,
                    PingIntervalMs = mqtt.PingIntervalMs,
                    UsePassword = mqttRequiresPassword,
                    TopicFilter = source.TopicFilter,
                    Qos = source.Qos,
                    ProcessRetained = source.ProcessRetained,
                    Fields = JsonFields(source.Fields),
                    UpstreamTime = JsonSourceValidation.Time(source.UpstreamTime),
                }, SettingsJson);
                _ = MqttInputSettings.Parse(configuration);
                fields = Schema(source.Fields);
                break;
            case DeviceKind.Http:
                var http = connection!.Http!;
                var pull = input.Http!;
                var headers = new Dictionary<string, string>(http.Headers ?? [], StringComparer.OrdinalIgnoreCase);
                foreach (var header in pull.Headers ?? [])
                { headers[header.Key] = header.Value; }
                configuration = JsonSerializer.SerializeToElement(new HttpInputSettings
                {
                    ConnectionId = connection.Id,
                    BaseAddress = http.BaseAddress,
                    TimeoutMs = http.TimeoutMs,
                    Path = pull.Path,
                    PeriodMs = pull.PeriodMs,
                    Headers = headers.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase),
                    Fields = JsonFields(pull.Fields),
                    UpstreamTime = JsonSourceValidation.Time(pull.UpstreamTime),
                }, SettingsJson);
                _ = HttpInputSettings.Parse(configuration);
                fields = Schema(pull.Fields);
                break;
            case DeviceKind.Simulator:
                var simulator = input.Simulator!;
                var options = new Dictionary<string, object?>
                {
                    ["periodMs"] = simulator.PeriodMs,
                    ["faults"] = new { simulator.MissingField, simulator.InvalidValue, simulator.ConnectionError },
                };
                if (simulator.SampleJson is { } sample)
                { options["sample"] = JsonSerializer.Deserialize<JsonElement>(sample); }
                else
                { options["template"] = simulator.Template ?? "meter"; }
                configuration = JsonSerializer.SerializeToElement(options, Json);
                fields = SimulatorInputFactory.Describe(new RuntimeUnitPlan(id, configuration), device.Key, input.Key)
                    .Select(field => new MappedFieldSchema(field.Key, field.Value, null)).ToImmutableArray();
                break;
            default:
                throw new ArgumentException("未知来源类型。");
        }
        return new(new(input.Id, id, input.Enabled && device.Enabled && (connection?.Enabled ?? true), fields), new(id, configuration));
    }

    private static ImmutableArray<MappedFieldSchema> Schema(JsonSourceField[] fields) => fields.Select(field => new MappedFieldSchema(field.Key, field.Type!.Value, field.Unit)).ToImmutableArray();
    private static JsonElement JsonFields(JsonSourceField[] fields) => JsonSerializer.SerializeToElement(fields.Select(field => new { field.Key, field.Path, field.Type }), Json);
}
