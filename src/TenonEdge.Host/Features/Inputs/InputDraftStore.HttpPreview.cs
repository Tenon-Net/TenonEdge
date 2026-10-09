using System.Collections.Immutable;
using System.Text.Json;
using TenonEdge.Adapters.Http;
using TenonEdge.Host.Features.Configuration;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Devices;

namespace TenonEdge.Host.Features.Inputs;

internal sealed partial class InputDraftStore
{
    internal HttpPreviewSource[] ReadHttpPreview(long deviceId, HttpReadPreviewRequest request) => database.InTransaction(client =>
    {
        if (string.IsNullOrEmpty(request.ExpectedVersion) || request.ExpectedVersion.Length > 64)
        { throw new ArgumentException("采样须提供草稿版本。"); }
        ConfigurationDraft.CheckVersion(client, request.ExpectedVersion);
        var device = DeviceDraftStore.Read(client, deviceId) ?? throw new DraftWriteException("device.notFound");
        if (device.Kind != DeviceKind.Http || device.ConnectionId is null)
        { throw new ArgumentException("采样设备不是 HTTP 设备。"); }
        var connection = ConnectionDraftStore.Read(client, device.ConnectionId.Value);
        if (connection?.Kind != ConnectionKind.Http || connection.Http is not { } transport)
        { throw new DraftWriteException("connection.notFound"); }
        var inputs = ReadForDevice(client, deviceId);
        var selected = request.InputId is null ? null : inputs.SingleOrDefault(input => input.Id == request.InputId)
            ?? throw new DraftWriteException("input.notFound");
        var candidates = request.Source is { } source
            ? [InputDraftValidation.Normalize(selected?.Id ?? 1, device,
                new(request.ExpectedVersion, selected?.Key ?? "unsaved", selected?.Name ?? "未保存来源", Http: source), connection)]
            : selected is null ? inputs.Where(input => input.Enabled).ToArray() : [selected];
        if (candidates.Length == 0)
        { throw new ArgumentException("没有可采样的 HTTP 来源。"); }
        return candidates.Select(input =>
        {
            var normalized = JsonSourceValidation.Normalize(input.Http ?? throw new ArgumentException("HTTP 来源配置缺失。"), connection);
            var headers = new Dictionary<string, string>(transport.Headers ?? [], StringComparer.OrdinalIgnoreCase);
            foreach (var header in normalized.Headers ?? [])
            { headers[header.Key] = header.Value; }
            var settings = new HttpInputSettings
            {
                ConnectionId = connection.Id,
                BaseAddress = transport.BaseAddress,
                TimeoutMs = transport.TimeoutMs,
                Path = normalized.Path,
                PeriodMs = normalized.PeriodMs,
                Headers = headers.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase),
                Fields = JsonSerializer.SerializeToElement(normalized.Fields.Select(field => new { field.Key, field.Path, field.Type }), Json),
                UpstreamTime = JsonSourceValidation.Time(normalized.UpstreamTime)
            };
            return new HttpPreviewSource(input.Key, settings);
        }).ToArray();
    });
}
