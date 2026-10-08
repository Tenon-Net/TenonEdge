using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Abstractions.Plans;

namespace TenonEdge.Adapters.Mqtt;

/// <summary>T1 的种子输出配置。连接地址与报文配置不含秘密；输入引用仍为明确的 deviceKey/inputKey。</summary>
public sealed record MqttOutputSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public long ConnectionId { get; init; }
    public string Host { get; init; } = string.Empty;
    public int Port { get; init; } = 1883;
    public bool Tls { get; init; }
    public bool UsePassword { get; init; }
    public string Topic { get; init; } = string.Empty;
    public int Qos { get; init; } = 1;
    public int TimeoutMs { get; init; } = 5000;
    public int PingIntervalMs { get; init; } = 15000;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement Mapping { get; init; }
    public string? ClientId { get; init; }
    public string[] Inputs { get; init; } = [];

    public string Recipient => $"{(Tls ? "mqtts" : "mqtt")}://{(Host.Contains(':', StringComparison.Ordinal) ? "[" + Host.ToLowerInvariant() + "]" : Host.ToLowerInvariant())}:{Port}";

    public static MqttOutputSettings Parse(JsonElement element)
    {
        var settings = element.Deserialize<MqttOutputSettings>(JsonOptions) ?? throw new ArgumentException("MQTT 配置不能为空。");
        ArgumentOutOfRangeException.ThrowIfLessThan(settings.ConnectionId, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(settings.ConnectionId, 9007199254740991L);
        if (string.IsNullOrWhiteSpace(settings.Host) || settings.Host.Length > 253 || Uri.CheckHostName(settings.Host) == UriHostNameType.Unknown)
        {
            throw new ArgumentException("MQTT 主机应为主机名或 IP 地址。");
        }

        if (Uri.CheckHostName(settings.Host) == UriHostNameType.IPv6)
        {
            // 与 URI 凭证绑定使用同一规范形式；TCP 客户端接受不带方括号的 IPv6 地址。
            settings = settings with { Host = IPAddress.Parse(settings.Host.Trim('[', ']')).ToString() };
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(settings.Port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(settings.Port, 65535);
        ArgumentOutOfRangeException.ThrowIfLessThan(settings.TimeoutMs, 100);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(settings.TimeoutMs, 60000);
        ArgumentOutOfRangeException.ThrowIfLessThan(settings.PingIntervalMs, 1000);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(settings.PingIntervalMs, 60000);
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

        if (settings.ClientId is not null && string.IsNullOrWhiteSpace(settings.ClientId) || settings.ClientId is { Length: > 128 } || settings.ClientId?.Contains('\0', StringComparison.Ordinal) == true)
        {
            throw new ArgumentException("ClientId 无效。");
        }

        if (settings.Topic is null)
        {
            throw new ArgumentException("MQTT Topic 不能为空。");
        }

        ValidateTopic(settings.Topic.Replace("{deviceKey}", "device", StringComparison.Ordinal));
        return settings;
    }

    public string TopicFor(string deviceKey)
    {
        BusinessKeys.Validate(deviceKey, nameof(deviceKey));
        var topic = Topic.Replace("{deviceKey}", deviceKey, StringComparison.Ordinal);
        ValidateTopic(topic);
        return topic;
    }

    public string Fingerprint(string? username, string? accountMarker) => "sha256:" + Convert.ToHexStringLower(
        SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new[] { Recipient, username ?? "", accountMarker ?? "" }))));

    private static void ValidateTopic(string topic)
    {
        if (string.IsNullOrEmpty(topic) || Encoding.UTF8.GetByteCount(topic) > 65535 || topic.IndexOfAny(['+', '#', '\0', '{', '}']) >= 0)
        {
            throw new ArgumentException("MQTT Topic 不能为空，不能含通配符、空字符或未知占位符。");
        }
    }
}
