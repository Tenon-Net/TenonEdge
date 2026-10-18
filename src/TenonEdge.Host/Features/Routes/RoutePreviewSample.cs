using System.Globalization;
using System.Text;
using System.Text.Json;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Host.Features.Devices;
using TenonEdge.Runtime.Mapping;

namespace TenonEdge.Host.Features.Routes;

internal sealed record RouteDecodedSample(EdgeMessage Message, bool InvalidUpstreamTime);

/// <summary>手工样本不是一次采集。JSON 协议按已保存来源的定位/类型/时间解码；
/// Modbus/模拟来源使用已解码字段对象，不模拟寄存器、字节序或故障注入。</summary>
internal static class RoutePreviewSample
{
    internal const int MaximumBytes = 64 * 1024;
    internal static RouteDecodedSample Decode(RouteCompilationSnapshot snapshot, long sourceId, string sampleJson,
        DateTimeOffset receivedAt, ClockSyncState clock, CancellationToken token)
    {
        try
        { return DecodeCore(snapshot, sourceId, sampleJson, receivedAt, clock, token); }
        catch (InvalidOperationException error)
        { throw new ArgumentException("手工样本的 JSON 字符或来源配置无效。", nameof(sampleJson), error); }
    }

    private static RouteDecodedSample DecodeCore(RouteCompilationSnapshot snapshot, long sourceId, string sampleJson,
        DateTimeOffset receivedAt, ClockSyncState clock, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(sampleJson) || sampleJson.Length > MaximumBytes || Encoding.UTF8.GetByteCount(sampleJson) > MaximumBytes)
        { throw new ArgumentException("手工样本须为不超过 64 KiB 的 JSON。"); }
        if (!snapshot.Inputs.TryGetValue(sourceId, out var input) || !snapshot.Devices.TryGetValue(input.DeviceId, out var device))
        { throw new RouteTopologyException(new("source.missing", snapshot.Routes[0].Key, SourceId: sourceId)); }
        var connection = device.ConnectionId is { } id ? snapshot.Connections.GetValueOrDefault(id) : null;
        var compiled = RouteSourceCompiler.Compile(input, device, connection, false, snapshot.InputsCatalog);
        using var document = JsonDocument.Parse(sampleJson, new JsonDocumentOptions { MaxDepth = 64 });
        var root = document.RootElement;
        receivedAt = receivedAt.ToUniversalTime();
        if (input.Kind is DeviceKind.Http or DeviceKind.Mqtt)
        {
            var configuration = compiled.Unit.Configuration;
            var decoder = new JsonInputDecoderFactory().Build(configuration.GetProperty("fields"),
                configuration.TryGetProperty("upstreamTime", out var time) ? time : default);
            var decoded = decoder.Decode(root, receivedAt, clock, token);
            return new(new EdgeMessage(Guid.CreateVersion7(receivedAt), input.Key, device.Key, decoded.CollectedAt, decoded.TimeSource,
                receivedAt, decoded.Values, decoded.Quality, RawJsonPayload.CopyFrom(root)), decoded.InvalidUpstreamTime);
        }
        if (root.ValueKind != JsonValueKind.Object)
        { throw new ArgumentException("Modbus/模拟来源的手工样本须为字段对象。"); }
        var values = new Dictionary<string, FieldValue>(StringComparer.Ordinal);
        var quality = new Dictionary<string, QualityCode>(StringComparer.Ordinal);
        var unsigned = input.Modbus?.Points.Where(point => point.Enabled && point.Type == "uint64").Select(point => point.Key).ToHashSet(StringComparer.Ordinal) ?? [];
        long bytes = 0;
        foreach (var field in compiled.Source.Fields)
        {
            token.ThrowIfCancellationRequested();
            var pointer = new JsonFieldPointer("/" + field.Key.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal));
            if (!pointer.TryResolve(root, out var value))
            { continue; }
            var converted = unsigned.Contains(field.Key) ? Unsigned(value) : JsonFieldValueReader.Read(value, field.Type);
            bytes += 256L + 2L * field.Key.Length + (converted.Value?.BudgetBytes ?? 0);
            if (bytes > FieldMapping.MaximumResultBytes)
            { throw new InputMessageSizeException("手工样本解码结果超过 4 MiB。"); }
            if (converted.Value is { } good)
            { values.Add(field.Key, good); }
            else
            { quality.Add(field.Key, converted.Error ?? QualityCode.ConversionFailed); }
        }
        // Actual Modbus/simulator messages have no RawJson; do not make Pointer rules work only in preview.
        return new(new EdgeMessage(Guid.CreateVersion7(receivedAt), input.Key, device.Key, receivedAt,
            clock == ClockSyncState.NotSynchronized ? TimeSource.GatewayUnsynced : TimeSource.Gateway,
            receivedAt, values, quality), false);
    }

    private static FieldConversionResult Unsigned(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetUInt64(out var numeric))
        { return new(FieldValue.FromUInt64(numeric), null); }
        if (value.ValueKind == JsonValueKind.String && ulong.TryParse(value.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var text))
        { return new(FieldValue.FromUInt64(text), null); }
        return new(null, JsonFieldValueReader.Read(value, FieldType.Integer).Error ?? QualityCode.Overflow);
    }
}
