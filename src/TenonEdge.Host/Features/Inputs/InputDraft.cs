using System.Text.Json.Serialization;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Adapters.Modbus;
using TenonEdge.Host.Features.Devices;

namespace TenonEdge.Host.Features.Inputs;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ModbusSourceDraft(int PeriodMs, ModbusPointSettings[] Points);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record InputDraftRequest(string ExpectedVersion, string Key, string Name, bool Enabled = true,
    ModbusSourceDraft? Modbus = null);

internal sealed record InputDraft(long Id, long DeviceId, string Key, string Name, DeviceKind Kind, bool Enabled,
    ModbusSourceDraft? Modbus);
internal sealed record InputDraftList(string Version, InputDraft[] Inputs);
internal sealed record InputDraftResult(string Version, InputDraft Input);

internal static class InputDraftValidation
{
    public static InputDraft Normalize(long id, DeviceDraft device, InputDraftRequest request)
    {
        if (string.IsNullOrEmpty(request.ExpectedVersion) || request.ExpectedVersion.Length > 64 ||
            !BusinessKeys.IsValid(request.Key) || string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 64 ||
            request.Name.Any(char.IsControl))
        { throw new ArgumentException("来源 key、名称或草稿版本无效。"); }
        if (device.Kind != DeviceKind.Modbus || request.Modbus is not { } modbus)
        { throw new ArgumentException("当前来源草稿接口须提供 Modbus 设备的采集组参数。"); }
        if (modbus.PeriodMs is < 1000 or > 86400000 || modbus.Points is null || modbus.Points.Length > 10000)
        { throw new ArgumentException("采集周期须为 1 秒至 1 天，点表最多 10000 项。"); }
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var point in modbus.Points)
        {
            if (point is null || !keys.Add(point.Key))
            { throw new ArgumentException("点位不能为空或重复。"); }
            point.ValidateDefinition();
        }
        // 空组与全停用点表可保存为草稿；编译时决定是否产生运行单元，不伪造启用点位。
        return new(id, device.Id, request.Key, request.Name.Trim(), device.Kind, request.Enabled,
            modbus with { Points = modbus.Points.ToArray() });
    }
}
