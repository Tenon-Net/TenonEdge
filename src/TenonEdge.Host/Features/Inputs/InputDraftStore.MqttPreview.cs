using System.Text.Json;
using TenonEdge.Adapters.Mqtt;
using TenonEdge.Host.Features.Configuration;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Credentials;
using TenonEdge.Host.Features.Devices;

namespace TenonEdge.Host.Features.Inputs;

internal sealed partial class InputDraftStore
{
    internal MqttInputSettings ReadMqttPreview(long deviceId, MqttReadPreviewRequest request) => database.InTransaction(client =>
    {
        if (string.IsNullOrEmpty(request.ExpectedVersion) || request.ExpectedVersion.Length > 64)
        { throw new ArgumentException("采样须提供草稿版本。"); }
        ConfigurationDraft.CheckVersion(client, request.ExpectedVersion);
        var device = DeviceDraftStore.Read(client, deviceId) ?? throw new DraftWriteException("device.notFound");
        if (device.Kind != DeviceKind.Mqtt || device.ConnectionId is null)
        { throw new ArgumentException("采样设备不是 MQTT 设备。"); }
        var connection = ConnectionDraftStore.Read(client, device.ConnectionId.Value);
        if (connection?.Kind != ConnectionKind.Mqtt || connection.Mqtt is not { } transport)
        { throw new DraftWriteException("connection.notFound"); }
        var selected = request.InputId is null ? null : ReadForDevice(client, deviceId).SingleOrDefault(input => input.Id == request.InputId)
            ?? throw new DraftWriteException("input.notFound");
        var source = request.Source ?? selected?.Mqtt ?? throw new ArgumentException("须指定采样来源。");
        var candidate = InputDraftValidation.Normalize(selected?.Id ?? 1, device,
            new(request.ExpectedVersion, selected?.Key ?? "unsaved", selected?.Name ?? "未保存来源", Mqtt: source), connection);
        var normalized = candidate.Mqtt!;
        return new MqttInputSettings
        {
            ConnectionId = connection.Id,
            Host = transport.Host,
            Port = transport.Port,
            Tls = transport.Tls,
            ClientId = transport.ClientId,
            TimeoutMs = transport.TimeoutMs,
            PingIntervalMs = transport.PingIntervalMs,
            // 已作废/接收方失配的凭证记录仍要求认证，绝不降级匿名。
            UsePassword = client.Queryable<MqttCredentialRecord>().Any(record => record.ConnectionId == connection.Id),
            TopicFilter = normalized.TopicFilter,
            Qos = normalized.Qos,
            ProcessRetained = normalized.ProcessRetained,
            Fields = JsonSerializer.SerializeToElement(normalized.Fields.Select(field => new { field.Key, field.Path, field.Type }), Json),
            UpstreamTime = JsonSourceValidation.Time(normalized.UpstreamTime)
        };
    });
}
