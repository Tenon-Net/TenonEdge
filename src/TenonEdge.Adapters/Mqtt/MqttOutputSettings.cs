using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Abstractions.Plans;

namespace TenonEdge.Adapters.Mqtt;

/// <summary>T1 的种子输出配置。连接地址与报文配置不含秘密；输入引用仍为明确的 deviceKey/inputKey。</summary>
public sealed record MqttOutputSettings : MqttConnectionSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public string Topic { get; init; } = string.Empty;
    public int Qos { get; init; } = 1;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement Mapping { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement MappingStages { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement Payload { get; init; }
    public string[] Inputs { get; init; } = [];

    public static MqttOutputSettings Parse(JsonElement element)
    {
        var settings = element.Deserialize<MqttOutputSettings>(JsonOptions) ?? throw new ArgumentException("MQTT 配置不能为空。");
        settings = (MqttOutputSettings)settings.ValidateConnection();
        if (settings.Qos is not (0 or 1) || settings.Inputs is null || settings.Inputs.Length == 0 || settings.Inputs.Length > 2000)
        {
            throw new ArgumentException("MQTT QoS 只能为 0 或 1，且至少引用一个输入（最多 2000）。");
        }

        foreach (var input in settings.Inputs)
        {
            if (string.IsNullOrEmpty(input) || RuntimeUnitId.Create("input.simulator", input).KeySegments.Count != 2)
            {
                throw new ArgumentException("输入引用应为 deviceKey/inputKey 两段。");
            }
        }

        if (settings.Inputs.Distinct(StringComparer.Ordinal).Count() != settings.Inputs.Length)
        {
            throw new ArgumentException("输入引用不能重复。");
        }

        if (settings.Topic is null)
        {
            throw new ArgumentException("MQTT Topic 不能为空。");
        }

        ValidateTopic(settings.Topic.Replace("{deviceKey}", "device", StringComparison.Ordinal));
        _ = OutputPayloadFormat.Parse(settings.Payload);
        return settings;
    }

    public string TopicFor(string deviceKey)
    {
        BusinessKeys.Validate(deviceKey, nameof(deviceKey));
        var topic = Topic.Replace("{deviceKey}", deviceKey, StringComparison.Ordinal);
        ValidateTopic(topic);
        return topic;
    }

    private static void ValidateTopic(string topic)
    {
        if (string.IsNullOrEmpty(topic) || Encoding.UTF8.GetByteCount(topic) > 65535 || topic.IndexOfAny(['+', '#', '\0', '{', '}']) >= 0)
        {
            throw new ArgumentException("MQTT Topic 不能为空，不能含通配符、空字符或未知占位符。");
        }
    }
}
