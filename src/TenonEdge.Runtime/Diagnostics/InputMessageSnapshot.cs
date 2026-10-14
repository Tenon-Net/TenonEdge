using System.Text.Json;
using TenonEdge.Abstractions.Messages;

namespace TenonEdge.Runtime.Diagnostics;

/// <summary>
/// 内部版本化诊断格式，不是输出信封。只保留编码前缀，同时计算完整编码字节数；
/// 禁止用截断前缀解码。不持有原消息，原始 JSON 也不作为旁路对象保留。
/// </summary>
internal static class InputMessageSnapshot
{
    internal static (byte[] Prefix, int OriginalBytes) Encode(EdgeMessage message, bool retainBody = true)
    {
        using var stream = new DiagnosticSnapshotWriter.PrefixStream(retainBody);
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", 2);
            writer.WriteNumber("diagnosticAccess", (int)message.DiagnosticAccess);
            writer.WritePropertyName("sampleMetadata");
            if (message.SampleMetadata is not { } metadata)
            { writer.WriteNullValue(); }
            else
            {
                writer.WriteStartObject();
                if (metadata.InvalidUpstreamTime is { } invalid)
                { writer.WriteBoolean("invalidUpstreamTime", invalid); }
                else
                { writer.WriteNull("invalidUpstreamTime"); }
                writer.WritePropertyName("mqtt");
                if (metadata.Mqtt is not { } mqtt)
                { writer.WriteNullValue(); }
                else
                {
                    writer.WriteStartObject();
                    writer.WriteBoolean("retained", mqtt.Retained);
                    writer.WriteBoolean("duplicate", mqtt.Duplicate);
                    writer.WriteNumber("qos", mqtt.Qos);
                    writer.WriteEndObject();
                }
                writer.WriteEndObject();
            }
            writer.WriteString("sourceMessageId", message.SourceMessageId);
            writer.WriteString("inputId", message.InputId);
            writer.WriteString("deviceKey", message.DeviceKey);
            writer.WriteString("collectedAt", message.CollectedAt);
            writer.WriteNumber("timeSource", (int)message.TimeSource);
            writer.WriteString("receivedAt", message.ReceivedAt);
            writer.WriteStartArray("values");
            foreach (var (key, value) in message.Values)
            {
                writer.WriteStartArray();
                DiagnosticSnapshotWriter.WriteText(writer, key);
                value.WriteSnapshot(writer);
                writer.WriteEndArray();
                writer.Flush();
            }
            writer.WriteEndArray();
            writer.WriteStartArray("quality");
            foreach (var (key, quality) in message.Quality)
            {
                writer.WriteStartArray();
                DiagnosticSnapshotWriter.WriteText(writer, key);
                writer.WriteNumberValue((int)quality);
                writer.WriteEndArray();
                writer.Flush();
            }
            writer.WriteEndArray();
            writer.WritePropertyName("rawJson");
            if (message.RawJson is null)
            { writer.WriteNullValue(); }
            else
            {
                // RawJson 已限制到 1 MiB。只临时获取这份有界文本，分段转义，
                // 避免为转义后的完整快照分配缓冲；缓存永不持有该文本/文档。
                DiagnosticSnapshotWriter.WriteText(writer, message.RawJson.Root.GetRawText());
            }
            writer.WriteEndObject();
        }
        return (stream.Prefix(), checked((int)stream.Length));
    }

    internal static EdgeMessage Decode(ReadOnlyMemory<byte> body)
    {
        if (body.Length > RuntimeMessageCache.MaximumRecordBytes)
        { throw new JsonException("完整诊断样本超过记录预算。"); }
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        if (root.GetProperty("version").GetInt32() != 2)
        { throw new JsonException("未知的诊断样本版本。"); }
        RawJsonPayload? raw = null;
        if (root.GetProperty("rawJson").ValueKind != JsonValueKind.Null)
        {
            using var rawDocument = JsonDocument.Parse(root.GetProperty("rawJson").GetString()!);
            raw = RawJsonPayload.CopyFrom(rawDocument.RootElement);
        }
        return new EdgeMessage(
            root.GetProperty("sourceMessageId").GetGuid(),
            root.GetProperty("inputId").GetString()!,
            root.GetProperty("deviceKey").GetString()!,
            root.GetProperty("collectedAt").GetDateTimeOffset(),
            (TimeSource)root.GetProperty("timeSource").GetInt32(),
            root.GetProperty("receivedAt").GetDateTimeOffset(),
            root.GetProperty("values").EnumerateArray().Select(item =>
                new KeyValuePair<string, FieldValue>(item[0].GetString()!, FieldValue.ReadSnapshot(item[1]))),
            root.GetProperty("quality").EnumerateArray().Select(item =>
                new KeyValuePair<string, QualityCode>(item[0].GetString()!, (QualityCode)item[1].GetInt32())),
            raw, (DiagnosticSampleAccess)root.GetProperty("diagnosticAccess").GetInt32(), ReadMetadata(root.GetProperty("sampleMetadata")));
    }

    private static InputSampleMetadata? ReadMetadata(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null)
        { return null; }
        var invalid = value.GetProperty("invalidUpstreamTime");
        var mqtt = value.GetProperty("mqtt");
        return new(invalid.ValueKind == JsonValueKind.Null ? null : invalid.GetBoolean(),
            mqtt.ValueKind == JsonValueKind.Null ? null : new(mqtt.GetProperty("retained").GetBoolean(),
                mqtt.GetProperty("duplicate").GetBoolean(), mqtt.GetProperty("qos").GetInt32()));
    }

}
