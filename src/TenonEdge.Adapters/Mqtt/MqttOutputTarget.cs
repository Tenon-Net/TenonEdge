using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Abstractions.Plans;

namespace TenonEdge.Adapters.Mqtt;

internal sealed class MqttOutputTarget(
    RuntimeUnitId id, MqttOutputSettings settings, ICredentialResolver credentials, TimeProvider time, string defaultClientPrefix, IOutboundConnector connector, ISharedConnections connections, MqttConnectionGroup? inputGroup = null) : IOutputTarget
{
    private readonly OutputPayloadFormat _payload = OutputPayloadFormat.Parse(settings.Payload);
    // 摘要的UTF-16结果逐路径保留；编码/拼接暂存与报文编码串行，只在输入上预约一次。
    public long MinimumSerializationReservationBytes => Math.Max(_payload.MinimumSerializationReservationBytes, 4L * OutputRecipientIdentity.MaximumSummaryBytes);
    public long FormattingReservationBytes => 2L * StandardEnvelope.MaximumPayloadBytes + 2L * OutputRecipientIdentity.MaximumSummaryBytes;
    public RuntimeUnitId Id => id;
    public long ConnectionId => settings.ConnectionId;
    public IReadOnlyList<string> Inputs { get; } = Array.AsReadOnly(settings.Inputs);
    public string ClientId { get; } = settings.ClientId ?? $"{defaultClientPrefix}-{settings.ConnectionId}";
    public string Recipient => settings.Recipient;
    public string CredentialVersion => Identity()?.Version ?? "unavailable";
    public TimeSpan SendTimeout => TimeSpan.FromMilliseconds(settings.TimeoutMs);
    public string CurrentFingerprint
    {
        get
        {
            var identity = Identity();
            return settings.Fingerprint(identity?.Username, identity?.AccountMarker);
        }
    }
    private MqttCredentialIdentity? Identity() => settings.UsePassword ? credentials.DescribeMqtt(ConnectionId) : null;

    public OutputTargetIdentity CaptureIdentity()
    {
        var identity = Identity();
        return new(settings.Fingerprint(identity?.Username, identity?.AccountMarker),
            OutputRecipientIdentity.Mqtt(Recipient, settings.UsePassword, identity));
    }

    public OutputMessage CreateMessage(EdgeMessage message, Guid deliveryId)
    {
        // 暂停期间仍按当前非秘密账户身份入队；只有 SendAsync 才取得接收方绑定的秘密。
        var identity = CaptureIdentity();
        return new OutputMessage(_payload.Serialize(message, deliveryId), settings.TopicFor(message.DeviceKey), settings.Qos,
            identity.Fingerprint, identity.Summary);
    }

    private string ConnectionSettings => settings.ConnectionIdentity(ClientId);

    public bool CanShareSenderWith(IOutputTarget other) => other is MqttOutputTarget mqtt &&
        ConnectionId == mqtt.ConnectionId && ConnectionSettings == mqtt.ConnectionSettings && ReferenceEquals(inputGroup, mqtt.InputGroup);

    public bool QuiesceWithInputs => inputGroup is not null;
    private MqttConnectionGroup? InputGroup => inputGroup;

    public IOutputAdapter CreateAdapter() => inputGroup?.CreateOutputAdapter(settings, ClientId) ?? new MqttOutputAdapter(
        connections.Acquire(ConnectionId, "mqtt", ConnectionSettings, () => new MqttSession(settings, ClientId, credentials, time, connector)), time, settings.TimeoutMs);
}
