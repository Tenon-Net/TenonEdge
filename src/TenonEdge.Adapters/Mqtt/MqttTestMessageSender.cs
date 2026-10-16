using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;

namespace TenonEdge.Adapters.Mqtt;

public sealed record MqttTestSendResult(bool Sent, bool BrokerAcknowledged, string Code, double ElapsedMs);

/// <summary>一次显式测试的传输原语：复用正式会话的格式、TLS、接收上界和出站连接器，
/// 但使用独立随机 ClientId / CleanSession，不借用运行 ClientId、不重连重试、不入队。
/// Host 必须先核对权限、确认、凭证绑定及共享测试预算；本类不提供管理入口。</summary>
public sealed class MqttTestMessageSender(IOutboundConnector connector, TimeProvider time, ITlsTrustResolver? trust = null)
{
    public async Task<MqttTestSendResult> SendAsync(MqttConnectionSettings settings, MqttCredential? credential,
        OutputMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(message);
        settings = settings.ValidateConnection();
        if (settings.UsePassword && credential is null)
        { throw new ArgumentException("测试凭证与连接要求不一致。"); }
        if (credential is not null)
        { MqttCredentialValidation.Validate(credential, settings.Tls); }
        MqttOutputSettings.ValidateTopic(message.Address);
        if (message.QualityOfService is not (0 or 1) || message.Payload.IsEmpty || message.Payload.Length > StandardEnvelope.MaximumPayloadBytes ||
            message.Headers is { Count: > 0 } || message.ProtocolMetadata is not null)
        { throw new ArgumentException("MQTT 测试须使用完整、有效的冻结输出报文。"); }
        cancellationToken.ThrowIfCancellationRequested();
        var started = time.GetTimestamp();
        // Entire connect + publish lifetime is bounded, including a missing PUBACK.
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(settings.TimeoutMs), time);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var credentials = new SnapshotCredential(settings.ConnectionId, settings.Recipient, credential);
        await using var session = new MqttSession(settings, "te-send-test-" + Guid.NewGuid().ToString("N"), credentials, time, connector, trust: trust);
        try
        {
            var result = await session.SendAsync(message, operation.Token);
            cancellationToken.ThrowIfCancellationRequested();
            // MQTTnet may wrap cancellation as a communication failure; the
            // production session then returns Retry rather than throwing. This
            // test operation never retries, and must still report its deadline.
            if (result.Status != OutputSendStatus.Ack && deadline.IsCancellationRequested)
            { return Result(false, false, "mqtt.timeoutUnknown"); }
            return result.Status == OutputSendStatus.Ack
                ? Result(true, message.QualityOfService == 1, message.QualityOfService == 1 ? "mqtt.puback" : "mqtt.writtenWithoutAck")
                : Result(false, false, result.FingerprintMismatch ? "recipient.changed" : result.Status == OutputSendStatus.SuspendTarget
                    ? "connection.rejected" : "mqtt.deliveryUnknown");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        { return Result(false, false, "mqtt.timeoutUnknown"); }

        MqttTestSendResult Result(bool sent, bool acknowledged, string code) => new(sent, acknowledged, code, time.GetElapsedTime(started).TotalMilliseconds);
    }

    // One detached credential snapshot, never a request scope or database transaction.
    private sealed class SnapshotCredential(long id, string recipient, MqttCredential? value) : ICredentialResolver
    {
        public MqttCredentialIdentity? DescribeMqtt(long connectionId) => connectionId == id && value is not null
            ? value.Identity : null;
        public MqttCredential? ResolveMqtt(long connectionId, string expectedRecipient) => connectionId == id && expectedRecipient == recipient ? value : null;
    }
}
