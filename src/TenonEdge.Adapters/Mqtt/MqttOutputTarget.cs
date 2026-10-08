using System.Text.Json;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Abstractions.Plans;

namespace TenonEdge.Adapters.Mqtt;

internal sealed class MqttOutputTarget(
    RuntimeUnitId id, MqttOutputSettings settings, ICredentialResolver credentials, TimeProvider time, string defaultClientPrefix, IOutboundConnector connector, ISharedConnections connections, MqttConnectionGroup? inputGroup = null) : IOutputTarget
{
    private readonly OutputPayloadFormat _payload = OutputPayloadFormat.Parse(settings.Payload);
    public long MinimumSerializationReservationBytes => _payload.MinimumSerializationReservationBytes;
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
    private MqttCredentialIdentity? Identity() => settings.UsePassword ? credentials.DescribeMqtt(ConnectionId) : null;

    public OutputMessage CreateMessage(EdgeMessage message, Guid deliveryId)
    {
        // 暂停期间仍按当前非秘密账户身份入队；只有 SendAsync 才取得接收方绑定的秘密。
        var identity = Identity();
        return new OutputMessage(_payload.Serialize(message, deliveryId), settings.TopicFor(message.DeviceKey), settings.Qos,
            settings.Fingerprint(identity?.Username, identity?.AccountMarker), $"{Recipient}; authentication={(settings.UsePassword ? "password" : "anonymous")}; account={JsonSerializer.Serialize(identity?.Username ?? string.Empty)}");
    }

    private string ConnectionSettings => settings.ConnectionIdentity(ClientId);

    public bool CanShareSenderWith(IOutputTarget other) => other is MqttOutputTarget mqtt &&
        ConnectionId == mqtt.ConnectionId && ConnectionSettings == mqtt.ConnectionSettings && ReferenceEquals(inputGroup, mqtt.InputGroup);

    private MqttConnectionGroup? InputGroup => inputGroup;

    public IOutputAdapter CreateAdapter() => inputGroup?.CreateOutputAdapter(settings, ClientId) ?? new MqttOutputAdapter(
        connections.Acquire(ConnectionId, "mqtt", ConnectionSettings, () => new MqttSession(settings, ClientId, credentials, time, connector)), time, settings.TimeoutMs);
}
