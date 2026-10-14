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
    /// <summary>仅接续该连接已有的冻结队列，不是路线，也不从任何输入生成投递。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool QueueOnly { get; init; }
    /// <summary>仅用于旧完整快照的执行视图：缺失凭证引用时禁止猜测匿名认证，单独报告该发送单元构建失败。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool QueueCredentialReferenceMissing { get; init; }
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
        if (settings.QueueOnly)
        {
            if (settings.Inputs is not { Length: 0 } || settings.Topic != string.Empty || settings.Qos != 1 ||
                settings.Mapping.ValueKind != JsonValueKind.Undefined || settings.MappingStages.ValueKind != JsonValueKind.Undefined ||
                settings.Payload.ValueKind != JsonValueKind.Undefined)
            { throw new ArgumentException("仅发送冻结队列的 MQTT 目标不能声明输入、主题、QoS、映射或报文格式。"); }
            return settings;
        }
        if (settings.QueueCredentialReferenceMissing)
        { throw new ArgumentException("凭证引用缺失标志只适用于旧快照的队列接续单元。"); }
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

    internal static void ValidateTopic(string topic)
    {
        if (string.IsNullOrEmpty(topic) || Encoding.UTF8.GetByteCount(topic) > 65535 || topic.IndexOfAny(['+', '#', '\0', '{', '}']) >= 0)
        {
            throw new ArgumentException("MQTT Topic 不能为空，不能含通配符、空字符或未知占位符。");
        }
    }
}
