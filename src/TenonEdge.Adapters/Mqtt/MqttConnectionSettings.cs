using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TenonEdge.Adapters.Mqtt;

/// <summary>MQTT 物理连接的非秘密参数；不包含 Topic、消息格式、映射或输入定义。</summary>
public record MqttConnectionSettings
{
    public long ConnectionId { get; init; }
    public string Host { get; init; } = string.Empty;
    public int Port { get; init; } = 1883;
    public bool Tls { get; init; }
    public bool UsePassword { get; init; }
    public int TimeoutMs { get; init; } = 5000;
    public int PingIntervalMs { get; init; } = 15000;
    public string? ClientId { get; init; }

    public string Recipient => $"{(Tls ? "mqtts" : "mqtt")}://{(Host.Contains(':', StringComparison.Ordinal) ? "[" + Host.ToLowerInvariant() + "]" : Host.ToLowerInvariant())}:{Port}";

    internal MqttConnectionSettings ValidateConnection()
    {
        var settings = this;
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
        if (settings.ClientId is not null && string.IsNullOrWhiteSpace(settings.ClientId) || settings.ClientId is { Length: > 128 } || settings.ClientId?.Contains('\0', StringComparison.Ordinal) == true)
        {
            throw new ArgumentException("ClientId 无效。");
        }

        return settings;
    }

    /// <summary>配置一致性与共享租约使用同一个规范形式；调用方传实际 ClientId，冻结前校验可传尚未展开的 null。</summary>
    public string ConnectionIdentity(string? clientId) => JsonSerializer.Serialize(new
    {
        Host = Host.ToLowerInvariant(),
        Port,
        Tls,
        UsePassword,
        TimeoutMs,
        PingIntervalMs,
        ClientId = clientId,
    });

    public string Fingerprint(string? username, string? accountMarker) => "sha256:" + Convert.ToHexStringLower(
        SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new[] { Recipient, username ?? "", accountMarker ?? "" }))));

}
