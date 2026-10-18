using System.Text.Json.Serialization;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Adapters.Modbus;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Devices;
using TenonEdge.Runtime.Inputs;

namespace TenonEdge.Host.Features.Inputs;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ModbusSourceDraft(int PeriodMs, ModbusPointSettings[] Points);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record InputDraftRequest(string ExpectedVersion, string Key, string Name, bool Enabled = true,
    ModbusSourceDraft? Modbus = null, MqttSourceDraft? Mqtt = null, HttpSourceDraft? Http = null, SimulatorSourceDraft? Simulator = null,
    ModuleSourceDraft? Module = null);

internal sealed record InputDraft(long Id, long DeviceId, string Key, string Name, DeviceKind Kind, bool Enabled,
    ModbusSourceDraft? Modbus, MqttSourceDraft? Mqtt = null, HttpSourceDraft? Http = null, SimulatorSourceDraft? Simulator = null,
    ModuleSourceDraft? Module = null);
internal sealed record InputDraftList(string Version, InputDraft[] Inputs);
internal sealed record InputDraftResult(string Version, InputDraft Input);

internal static class InputDraftValidation
{
    public static InputDraft Normalize(long id, DeviceDraft device, InputDraftRequest request, ConnectionDraft? connection = null)
    {
        if (string.IsNullOrEmpty(request.ExpectedVersion) || request.ExpectedVersion.Length > 64 ||
            !BusinessKeys.IsValid(request.Key) || string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 64 ||
            request.Name.Any(char.IsControl))
        { throw new ArgumentException("来源 key、名称或草稿版本无效。"); }
        if ((request.Modbus is not null ? 1 : 0) + (request.Mqtt is not null ? 1 : 0) +
            (request.Http is not null ? 1 : 0) + (request.Simulator is not null ? 1 : 0) + (request.Module is not null ? 1 : 0) != 1)
        { throw new ArgumentException("须且只能提供设备协议对应的来源参数。"); }
        var input = new InputDraft(id, device.Id, request.Key, request.Name.Trim(), device.Kind, request.Enabled, null);
        switch (device.Kind)
        {
            case DeviceKind.Modbus when request.Modbus is { } modbus:
                ValidateModbus(modbus);
                return input with { Modbus = modbus with { Points = modbus.Points.ToArray() } };
            case DeviceKind.Mqtt when request.Mqtt is { } mqtt:
                if (connection?.Kind != ConnectionKind.Mqtt || connection.Id != device.ConnectionId)
                { throw new ArgumentException("MQTT 来源缺少对应连接。"); }
                JsonSourceValidation.Validate(mqtt);
                return input with { Mqtt = mqtt with { Fields = mqtt.Fields.ToArray() } };
            case DeviceKind.Http when request.Http is { } http:
                if (connection?.Kind != ConnectionKind.Http || connection.Id != device.ConnectionId)
                { throw new ArgumentException("HTTP 来源缺少对应连接。"); }
                return input with { Http = JsonSourceValidation.Normalize(http, connection) };
            case DeviceKind.Simulator when request.Simulator is { } simulator:
                return input with { Simulator = SimulatorSourceValidation.Normalize(simulator, device.Key, request.Key) };
            case DeviceKind.Module when request.Module is { } module:
                return input with { Module = module.Normalize(device, request.Key) };
            default:
                throw new ArgumentException("设备与来源协议不匹配。");
        }
    }

    public static IEnumerable<string> FieldKeys(InputDraft input, InputModuleCatalog? modules = null, IReadOnlyDictionary<long, ConnectionDraft>? connections = null) => input.Kind switch
    {
        DeviceKind.Modbus => input.Modbus!.Points.Select(point => point.Key),
        DeviceKind.Mqtt => input.Mqtt!.Fields.Select(field => field.Key),
        DeviceKind.Http => input.Http!.Fields.Select(field => field.Key),
        DeviceKind.Simulator => SimulatorSourceValidation.FieldKeys(input.Simulator!),
        DeviceKind.Module => (modules ?? InputModuleCatalog.Empty).Describe(input.Module!.ToUnit("metadata", input.Key, connections)).Select(field => field.Key),
        _ => throw new ArgumentException("来源类型无效。"),
    };

    private static void ValidateModbus(ModbusSourceDraft modbus)
    {
        if (modbus.PeriodMs is < 1000 or > 86400000 || modbus.Points is null || modbus.Points.Length > 10000)
        { throw new ArgumentException("采集周期须为 1 秒至 1 天，点表最多 10000 项。"); }
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var point in modbus.Points)
        {
            if (point is null || !keys.Add(point.Key))
            { throw new ArgumentException("点位不能为空或重复。"); }
            point.ValidateDefinition();
        }

    }
}
