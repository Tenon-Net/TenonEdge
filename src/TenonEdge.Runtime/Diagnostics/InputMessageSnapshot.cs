using System.Text.Json;
using TenonEdge.Abstractions.Messages;

namespace TenonEdge.Runtime.Diagnostics;

/// <summary>
/// 内部版本化诊断格式，不是输出信封。只保留编码前缀，同时计算完整编码字节数；
/// 禁止用截断前缀解码。不持有原消息，原始 JSON 也不作为旁路对象保留。
/// </summary>
internal static class InputMessageSnapshot
{
    internal static (byte[] Prefix, int OriginalBytes) Encode(EdgeMessage message)
    {
        using var stream = new PrefixStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", 1);
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
                WriteText(writer, key);
                value.WriteSnapshot(writer);
                writer.WriteEndArray();
                writer.Flush();
            }
            writer.WriteEndArray();
            writer.WriteStartArray("quality");
            foreach (var (key, quality) in message.Quality)
            {
                writer.WriteStartArray();
                WriteText(writer, key);
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
                WriteText(writer, message.RawJson.Root.GetRawText());
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
        if (root.GetProperty("version").GetInt32() != 1)
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
            raw);
    }

    private static void WriteText(Utf8JsonWriter writer, string text)
    {
        var remaining = text.AsSpan();
        while (remaining.Length > 512)
        {
            writer.WriteStringValueSegment(remaining[..512], isFinalSegment: false);
            remaining = remaining[512..];
            writer.Flush();
        }
        writer.WriteStringValueSegment(remaining, isFinalSegment: true);
    }

    // 默认 JSON 编码器将非 ASCII 转义，前缀始终为有效 UTF-8（但可能不是完整 JSON）。
    private sealed class PrefixStream : Stream
    {
        private readonly byte[] _prefix = new byte[RuntimeMessageCache.MaximumRecordBytes];
        private long _length;
        internal byte[] Prefix() => _prefix.AsSpan(0, (int)Math.Min(_length, _prefix.Length)).ToArray();
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            var remaining = (int)Math.Max(0, _prefix.Length - _length);
            buffer[..Math.Min(buffer.Length, remaining)].CopyTo(_prefix.AsSpan((int)Math.Min(_length, _prefix.Length)));
            _length = checked(_length + buffer.Length);
        }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override long Length => _length;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Position { get => _length; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
