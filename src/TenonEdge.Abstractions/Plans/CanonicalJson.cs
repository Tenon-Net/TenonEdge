using System.Buffers;
using System.Text.Json;

namespace TenonEdge.Abstractions.Plans;

/// <summary>
/// 运行单元配置的规范化 JSON，用于计算配置哈希：对象属性按名称的序号排序，去掉空白，字符串按同一规则重新转义；
/// 数值保留原文，因此 1、1.0 与 1E0 视为不同的配置。对象中有重名属性时拒绝，避免同一段配置有两种理解。
/// </summary>
public static class CanonicalJson
{
    public static byte[] Serialize(JsonElement element)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            Write(writer, element, "$");
        }

        return buffer.WrittenSpan.ToArray();
    }

    private static void Write(Utf8JsonWriter writer, JsonElement element, string path)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                WriteObject(writer, element, path);
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    Write(writer, item, $"{path}[{index}]");
                    index++;
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(element.GetRawText(), skipInputValidation: true);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw new ArgumentException($"配置中 {path} 不是有效的 JSON 值。", nameof(element));
        }
    }

    private static void WriteObject(Utf8JsonWriter writer, JsonElement element, string path)
    {
        var properties = element.EnumerateObject().ToList();
        properties.Sort(static (left, right) => string.CompareOrdinal(left.Name, right.Name));
        writer.WriteStartObject();
        for (var i = 0; i < properties.Count; i++)
        {
            var property = properties[i];
            if (i > 0 && string.Equals(properties[i - 1].Name, property.Name, StringComparison.Ordinal))
            {
                throw new ArgumentException($"配置中 {path} 有重名属性 {property.Name}。", nameof(element));
            }

            writer.WritePropertyName(property.Name);
            Write(writer, property.Value, $"{path}.{property.Name}");
        }

        writer.WriteEndObject();
    }
}
