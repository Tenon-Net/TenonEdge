using System.Text.Json;
using System.Text.Json.Serialization;

namespace TenonEdge.Adapters.Mqtt;

/// <summary>一个逻辑设备输入的订阅与字段；连接参数继续复用物理连接定义，不包含凭证秘密。</summary>
public sealed record MqttInputSettings : MqttConnectionSettings
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    public string TopicFilter { get; init; } = string.Empty;
    public int Qos { get; init; } = 1;
    public bool ProcessRetained { get; init; }
    public JsonElement Fields { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement UpstreamTime { get; init; }

    /// <summary>校验订阅结构；字段与时间的具体定义由 IJsonInputDecoderFactory 在构建输入时编译校验。</summary>
    public static MqttInputSettings Parse(JsonElement configuration)
    {
        var settings = configuration.Deserialize<MqttInputSettings>(Options) ?? throw new ArgumentException("MQTT 输入配置不能为空。");
        settings = (MqttInputSettings)settings.ValidateConnection();
        ValidateSubscription(settings.TopicFilter, settings.Qos, settings.ProcessRetained, settings.UpstreamTime);
        if (settings.Fields.ValueKind != JsonValueKind.Array || settings.Fields.GetArrayLength() is < 1 or > 1000)
        { throw new ArgumentException("MQTT 输入须定义 1—1000 个字段。"); }
        return settings with
        {
            Fields = settings.Fields.Clone(),
            UpstreamTime = settings.UpstreamTime.ValueKind == JsonValueKind.Undefined ? default : settings.UpstreamTime.Clone(),
        };
    }
    /// <summary>草稿与运行共用订阅约束，不连接 Broker；空字段草稿由编译阶段决定是否可运行。</summary>
    public static void ValidateSubscription(string topicFilter, int qos, bool processRetained, JsonElement upstreamTime = default)
    {
        MqttInputReceiver.ValidateFilter(topicFilter);
        if (qos is not (0 or 1))
        { throw new ArgumentException("MQTT 订阅只支持 QoS 0/1。"); }
        if (upstreamTime.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.Object))
        { throw new ArgumentException("上游时间映射须为对象。"); }
        if (processRetained && (upstreamTime.ValueKind != JsonValueKind.Object ||
            !upstreamTime.TryGetProperty("path", out var path) || path.ValueKind != JsonValueKind.String ||
            !upstreamTime.TryGetProperty("format", out var format) || format.ValueKind != JsonValueKind.String))
        { throw new ArgumentException("处理保留消息前须配置显式上游时间映射。"); }
    }

}
