using System.Collections.Immutable;
using TenonEdge.Adapters.Modbus;
using TenonEdge.Host.Features.Configuration;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Devices;

namespace TenonEdge.Host.Features.Inputs;

internal sealed record ModbusPreviewSnapshot(ModbusConnectionSettings Connection, byte UnitId, ModbusPreviewGroup[] Groups);

internal sealed partial class InputDraftStore
{
    // 版本、归属、设备与连接在同一短事务读出；返回纯快照，不把客户端或事务带到联网阶段。
    internal ModbusPreviewSnapshot ReadModbusPreview(long deviceId, ModbusReadPreviewRequest request) => database.InTransaction(client =>
    {
        if (string.IsNullOrEmpty(request.ExpectedVersion) || request.ExpectedVersion.Length > 64)
        { throw new ArgumentException("预览须提供草稿版本。"); }
        ConfigurationDraft.CheckVersion(client, request.ExpectedVersion);
        var device = DeviceDraftStore.Read(client, deviceId) ?? throw new DraftWriteException("device.notFound");
        if (device.Kind != DeviceKind.Modbus || device.ConnectionId is null || device.UnitId is null or < 0 or > 255)
        { throw new ArgumentException("预览设备不是有效的 Modbus 设备。"); }
        var connection = ConnectionDraftStore.Read(client, device.ConnectionId.Value);
        if (connection?.Kind != ConnectionKind.Modbus || connection.Modbus is not { } value)
        { throw new DraftWriteException("connection.notFound"); }
        var settings = new ModbusConnectionSettings
        {
            ConnectionId = connection.Id,
            Host = value.Host,
            Port = value.Port,
            Format = value.Format,
            ConnectTimeoutMs = value.ConnectTimeoutMs,
            ReadTimeoutMs = value.ReadTimeoutMs,
            RequestIntervalMs = value.RequestIntervalMs,
            RtuSilenceMs = value.RtuSilenceMs
        }.Validate();
        var inputs = ReadForDevice(client, deviceId);
        var selected = request.InputId is null ? null : inputs.SingleOrDefault(input => input.Id == request.InputId)
            ?? throw new DraftWriteException("input.notFound");
        ModbusPreviewGroup[] groups;
        if (request.Source is { } source)
        {
            // 复用草稿校验；未保存点表只用于这次请求，不能写入配置库。
            var candidate = InputDraftValidation.Normalize(selected?.Id ?? 1, device,
                new(request.ExpectedVersion, selected?.Key ?? "unsaved", selected?.Name ?? "未保存点表", Modbus: source), connection);
            groups = [new(candidate.Key, candidate.Modbus!.Points.ToImmutableArray())];
        }
        else
        {
            var candidates = selected is null ? inputs.Where(input => input.Enabled) : [selected];
            groups = candidates.Where(input => input.Modbus?.Points.Any(point => point.Enabled) == true)
                .Select(input => new ModbusPreviewGroup(input.Key, input.Modbus!.Points.ToImmutableArray())).ToArray();
        }
        if (groups.Length == 0)
        { throw new ArgumentException("没有可读取的启用点位。"); }
        return new ModbusPreviewSnapshot(settings, (byte)device.UnitId.Value, groups);
    });
}
