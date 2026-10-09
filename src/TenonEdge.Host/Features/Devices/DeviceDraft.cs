using System.Text.Json.Serialization;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Adapters.Modbus;
using TenonEdge.Host.Features.Connections;

namespace TenonEdge.Host.Features.Devices;

internal enum DeviceKind { Modbus, Mqtt, Http, Simulator }
internal enum ModbusAddressNotation { Offset, Manual }

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record DeviceDraftRequest(string ExpectedVersion, string Key, string Name, DeviceKind Kind,
    string? Group = null, long? ConnectionId = null, int? UnitId = null,
    ModbusAddressNotation AddressNotation = ModbusAddressNotation.Offset, bool Enabled = true);

internal sealed record DeviceDraft(long Id, string Key, string Name, DeviceKind Kind, string? Group,
    long? ConnectionId, int? UnitId, ModbusAddressNotation AddressNotation, bool Enabled);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record DeviceDraftCopyRequest(string ExpectedVersion, int Count);
internal sealed record DeviceDraftCopyResult(string Version, DeviceDraft[] Devices);

internal sealed record DeviceDraftList(string Version, DeviceDraft[] Devices);
internal sealed record DeviceDraftResult(string Version, DeviceDraft Device);

internal static class DeviceDraftValidation
{
    public static DeviceDraft Normalize(long id, DeviceDraftRequest request, ConnectionDraft? connection)
    {
        if (string.IsNullOrEmpty(request.ExpectedVersion) || request.ExpectedVersion.Length > 64 ||
            !BusinessKeys.IsValid(request.Key))
        { throw new ArgumentException("设备 key 或草稿版本无效。"); }
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 64 || request.Name.Any(char.IsControl) ||
            request.Group is { } group && (group.Length > 64 || group.Any(char.IsControl)))
        { throw new ArgumentException("名称必填且最多 64 个字符；分组为最多 64 个字符的单层文字。"); }
        if (!Enum.IsDefined(request.Kind) || !Enum.IsDefined(request.AddressNotation))
        { throw new ArgumentException("设备类型或地址写法无效。"); }
        var requiredKind = request.Kind switch
        {
            DeviceKind.Modbus => ConnectionKind.Modbus,
            DeviceKind.Mqtt => ConnectionKind.Mqtt,
            DeviceKind.Http => ConnectionKind.Http,
            _ => (ConnectionKind?)null,
        };
        if (requiredKind is null)
        {
            if (request.ConnectionId is not null)
            { throw new ArgumentException("模拟设备不能引用连接。"); }
        }
        else if (connection is null || request.ConnectionId != connection.Id || connection.Kind != requiredKind)
        { throw new ArgumentException("设备必须引用相应类型的已保存连接。"); }
        if (request.Kind == DeviceKind.Modbus)
        {
            if (request.UnitId is null or < 0 or > 255 ||
                connection!.Modbus!.Format == ModbusFrameFormat.RtuOverTcp && request.UnitId is < 1 or > 247)
            { throw new ArgumentException("Modbus TCP UnitId 为 0—255；RTU over TCP 为 1—247。"); }
        }
        else if (request.UnitId is not null || request.AddressNotation != ModbusAddressNotation.Offset)
        { throw new ArgumentException("只有 Modbus 设备可以指定 UnitId 与手册地址写法。"); }
        return new(id, request.Key, request.Name.Trim(), request.Kind,
            string.IsNullOrWhiteSpace(request.Group) ? null : request.Group.Trim(),
            request.ConnectionId, request.UnitId, request.AddressNotation, request.Enabled);
    }
}
