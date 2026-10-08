using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TenonEdge.Abstractions.Messages;

/// <summary>通用输出的纯 JSON 编码器。组装是显式字段选择，不解释表达式或字符串模板。</summary>
public sealed class OutputPayloadFormat
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8,
    };
    private readonly string _mode;
    private readonly Field[] _fields;
    private OutputPayloadFormat(string mode, Field[] fields) { _mode = mode; _fields = fields; }

    /// <summary>自定义字段可重复来源或附加常量，编码暂存不能仅按输入消息的大小估计。</summary>
    public long MinimumSerializationReservationBytes => _mode == "custom"
        ? StandardEnvelope.SerializationReservationBytes(StandardEnvelope.MaximumPayloadBytes) : 0;

    public static OutputPayloadFormat Parse(JsonElement configuration)
    {
        if (configuration.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        { return new("envelope", []); }
        var spec = configuration.Deserialize<Specification>(Options) ?? throw new ArgumentException("报文格式为空。");
        if (spec.Mode is not ("envelope" or "values" or "custom"))
        { throw new ArgumentException("未知报文格式。"); }
        if (spec.Mode != "custom")
        {
            if (spec.Fields is not null)
            { throw new ArgumentException("只有自定义格式允许字段组装。"); }
            return new(spec.Mode, []);
        }
        if (spec.Fields is null || spec.Fields.Length is < 1 or > 1000)
        { throw new ArgumentException("自定义组装须包含 1—1000 个字段。"); }
        var names = new HashSet<string>(StringComparer.Ordinal);
        var fields = new List<Field>();
        long constantBytes = 0;
        foreach (var field in spec.Fields)
        {
            if (field is null || string.IsNullOrWhiteSpace(field.Target) || field.Target.Length > 64 || !names.Add(field.Target))
            { throw new ArgumentException("组装字段名无效或重复。"); }
            var sources = (field.SourceField is null ? 0 : 1) + (field.Placeholder is null ? 0 : 1)
                + (field.Constant.ValueKind == JsonValueKind.Undefined ? 0 : 1);
            if (sources != 1 || field.SourceField is { Length: 0 or > 64 })
            { throw new ArgumentException("每个字段须选择来源字段、固定值或白名单占位符之一。"); }
            if (field.Placeholder is not (null or "{messageId}" or "{sourceMessageId}" or "{collectedAt}"))
            { throw new ArgumentException("占位符不在白名单中。"); }
            if (field.Constant.ValueKind is JsonValueKind.Object or JsonValueKind.Array ||
                field.Constant.ValueKind == JsonValueKind.Number && (!field.Constant.TryGetDouble(out var number) || !double.IsFinite(number)))
            { throw new ArgumentException("固定值须为有限 JSON 标量或 null。"); }
            if (field.Constant.ValueKind != JsonValueKind.Undefined)
            {
                constantBytes += Encoding.UTF8.GetByteCount(field.Constant.GetRawText());
                if (constantBytes > StandardEnvelope.MaximumPayloadBytes)
                { throw new ArgumentException("固定值总量超过报文大小上限。"); }
            }
            fields.Add(field with { Constant = field.Constant.ValueKind == JsonValueKind.Undefined ? default : field.Constant.Clone() });
        }
        return new(spec.Mode, fields.ToArray());
    }

    public byte[] Serialize(EdgeMessage message, Guid deliveryId)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (deliveryId == Guid.Empty)
        { throw new ArgumentException("投递编号不能为空。", nameof(deliveryId)); }
        if (_mode == "envelope")
        { return StandardEnvelope.Serialize(message, deliveryId); }
        var buffer = new StandardEnvelope.BoundedBuffer();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            if (_mode == "values")
            {
                foreach (var (name, value) in message.Values)
                { WriteValue(writer, name, value); }
            }
            else
            {
                foreach (var field in _fields)
                {
                    if (field.SourceField is { } source)
                    {
                        if (message.Values.TryGetValue(source, out var value))
                        { WriteValue(writer, field.Target, value); }
                        else if (field.Required)
                        { throw new ArgumentException("必填组装字段缺失或质量为 bad。"); }
                        continue;
                    }
                    writer.WritePropertyName(field.Target);
                    if (field.Placeholder is { } placeholder)
                    {
                        switch (placeholder)
                        {
                            case "{messageId}":
                                writer.WriteStringValue(deliveryId);
                                break;
                            case "{sourceMessageId}":
                                writer.WriteStringValue(message.SourceMessageId);
                                break;
                            case "{collectedAt}":
                                writer.WriteStringValue(StandardEnvelope.FormatTime(message.CollectedAt));
                                break;
                        }
                    }
                    else
                    { field.Constant.WriteTo(writer); }
                }
            }
            writer.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    private static void WriteValue(Utf8JsonWriter writer, string name, FieldValue value)
    {
        if (name.Length > StandardEnvelope.MaximumPayloadBytes ||
            value.Type == FieldType.String && value.ToString().Length > StandardEnvelope.MaximumPayloadBytes)
        { throw new ArgumentException("字段超过报文大小上限。"); }
        writer.WritePropertyName(name);
        value.WriteTo(writer);
    }

    private sealed record Specification(string Mode, Field[]? Fields = null);
    private sealed record Field(string Target, string? SourceField = null, string? Placeholder = null,
        JsonElement Constant = default, bool Required = false);
}
