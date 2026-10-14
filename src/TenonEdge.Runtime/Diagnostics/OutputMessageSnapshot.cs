using System.Text.Json;
using System.Text.Unicode;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;

namespace TenonEdge.Runtime.Diagnostics;

/// <summary>每条输出路径的映射结果与生成报文；不写凭证/指纹账户，不保留原始消息对象。</summary>
internal static class OutputMessageSnapshot
{
    internal static (byte[] Prefix, int OriginalBytes) Encode(EdgeMessage? mapped, OutputMessage? output, bool retainBody)
    {
        using var stream = new DiagnosticSnapshotWriter.PrefixStream(retainBody);
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", 1);
            writer.WritePropertyName("mapped");
            if (mapped is null)
            { writer.WriteNullValue(); }
            else
            {
                writer.WriteStartObject();
                writer.WriteString("collectedAt", mapped.CollectedAt);
                writer.WriteNumber("timeSource", (int)mapped.TimeSource);
                writer.WriteStartArray("values");
                foreach (var (key, value) in mapped.Values)
                {
                    writer.WriteStartArray();
                    DiagnosticSnapshotWriter.WriteText(writer, key);
                    value.WriteSnapshot(writer);
                    writer.WriteEndArray();
                    writer.Flush();
                }
                writer.WriteEndArray();
                writer.WriteStartArray("quality");
                foreach (var (key, value) in mapped.Quality)
                {
                    writer.WriteStartArray();
                    DiagnosticSnapshotWriter.WriteText(writer, key);
                    writer.WriteNumberValue((int)value);
                    writer.WriteEndArray();
                    writer.Flush();
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            writer.WritePropertyName("output");
            if (output is null)
            { writer.WriteNullValue(); }
            else
            {
                if (!Utf8.IsValid(output.Payload.Span))
                { throw new ArgumentException("诊断报文不是UTF-8文本。"); }
                writer.WriteStartObject();
                writer.WriteString("address", output.Address);
                if (output.QualityOfService is { } qos)
                { writer.WriteNumber("qos", qos); }
                else
                { writer.WriteNull("qos"); }
                writer.WriteStartObject("headers");
                foreach (var (key, value) in output.Headers ?? System.Collections.Immutable.ImmutableDictionary<string, string>.Empty)
                {
                    writer.WritePropertyName(key);
                    DiagnosticSnapshotWriter.WriteText(writer, value);
                }
                writer.WriteEndObject();
                writer.WritePropertyName("body");
                // The formal payload has already passed the 256KiB delivery limit.
                // Escape UTF-8 incrementally, without a full UTF-16 string copy.
                DiagnosticSnapshotWriter.WriteUtf8(writer, output.Payload.Span);
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
        }
        return (stream.Prefix(), checked((int)stream.Length));
    }
}
