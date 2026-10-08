using System.Text.Json;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Abstractions.Plans;

namespace TenonEdge.Adapters.Mqtt;

internal sealed class MqttOutputTarget(
    RuntimeUnitId id, MqttOutputSettings settings, ICredentialResolver credentials, TimeProvider time, string defaultClientPrefix, IOutboundConnector connector, ISharedConnections connections) : IOutputTarget
{
    public RuntimeUnitId Id => id;
    public long ConnectionId => settings.ConnectionId;
    public IReadOnlyList<string> Inputs { get; } = Array.AsReadOnly(settings.Inputs);
    public string ClientId { get; } = settings.ClientId ?? $"{defaultClientPrefix}-{settings.ConnectionId}";
    public string Recipient => settings.Recipient;
    public string CredentialVersion => Identity()?.Version ?? "unavailable";
    public string CurrentFingerprint
    {
        get
        {
            var identity = Identity();
            return settings.Fingerprint(identity?.Username, identity?.AccountMarker);
        }
    }
    internal MqttOutputSettings Settings => settings;
    internal TimeProvider Time => time;
    internal IOutboundConnector Connector => connector;
    private MqttCredentialIdentity? Identity() => settings.UsePassword ? credentials.DescribeMqtt(ConnectionId) : null;
    internal MqttCredential? Credential() => settings.UsePassword ? credentials.ResolveMqtt(ConnectionId, Recipient) : null;
    internal string Fingerprint(MqttCredential? credential) => settings.Fingerprint(credential?.Username, credential?.AccountMarker);

    public OutputMessage CreateMessage(EdgeMessage message, Guid deliveryId)
    {
        // 暂停期间仍按当前非秘密账户身份入队；只有 SendAsync 才取得接收方绑定的秘密。
        var identity = Identity();
        return new OutputMessage(StandardEnvelope.Serialize(message, deliveryId), settings.TopicFor(message.DeviceKey), settings.Qos,
            settings.Fingerprint(identity?.Username, identity?.AccountMarker), $"{Recipient}; authentication={(settings.UsePassword ? "password" : "anonymous")}; account={JsonSerializer.Serialize(identity?.Username ?? string.Empty)}");
    }

    public IOutputAdapter CreateAdapter()
    {
        var identity = JsonSerializer.Serialize(new
        {
            settings.Host, settings.Port, settings.Tls, settings.UsePassword,
            settings.TimeoutMs, settings.PingIntervalMs, ClientId,
        });
        return new MqttOutputAdapter(connections.Acquire(ConnectionId, "mqtt", identity, () => new MqttSession(this)), time, settings.TimeoutMs);
    }
}
