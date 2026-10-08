using System.Security.Authentication;
using MQTTnet;
using MQTTnet.Formatter;
using MQTTnet.Protocol;
using TenonEdge.Abstractions.Contracts;

namespace TenonEdge.Adapters.Mqtt;

/// <summary>单发送循环独占客户端，无回调重连。所有外部 I/O 有显式期限；异常文本不向运行事件泄露秘密。</summary>
internal sealed class MqttOutputAdapter(MqttOutputTarget target) : IOutputAdapter
{
    private IMqttClient? _client;
    private string? _connectedVersion;
    private long _lastPing;

    public async Task<OutputSendResult> SendAsync(OutputMessage message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var credential = target.Credential();
        if (target.Settings.UsePassword && credential is null)
        {
            Reset();
            return new(OutputSendStatus.SuspendTarget, "MQTT 凭证未设置、已作废或无法解密。");
        }

        if (!string.Equals(message.Fingerprint, target.Fingerprint(credential), StringComparison.Ordinal))
        {
            Reset();
            return new(OutputSendStatus.SuspendTarget, "接收方身份已变化，已停止发送旧记录。");
        }

        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(target.Settings.TimeoutMs), target.Time);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, cancellationToken);
        try
        {
            var version = credential?.Version ?? "anonymous";
            if (_connectedVersion != version)
            {
                Reset();
            }

            _client ??= new MqttClientFactory().CreateMqttClient();
            if (!_client.IsConnected)
            {
                var builder = new MqttClientOptionsBuilder()
                    .WithTcpServer(target.Settings.Host, target.Settings.Port)
                    .WithProtocolVersion(MqttProtocolVersion.V311)
                    .WithCleanSession(true)
                    .WithClientId(target.ClientId)
                    .WithKeepAlivePeriod(TimeSpan.FromMilliseconds(target.Settings.PingIntervalMs))
                    .WithTimeout(TimeSpan.FromMilliseconds(target.Settings.TimeoutMs));
                if (credential is not null)
                {
                    builder.WithCredentials(credential.Username, credential.Password);
                }

                if (target.Settings.Tls)
                {
                    builder.WithTlsOptions(tls => tls.UseTls());
                }

                var connected = await _client.ConnectAsync(builder.Build(), operation.Token);
                if (connected.ResultCode != MqttClientConnectResultCode.Success)
                {
                    Reset();
                    return new(connected.ResultCode == MqttClientConnectResultCode.ServerUnavailable ? OutputSendStatus.Retry : OutputSendStatus.SuspendTarget, "MQTT Broker 拒绝连接，请检查身份或协议配置。");
                }

                _connectedVersion = version;
                _lastPing = target.Time.GetTimestamp();
            }

            // 持续发送会抑制 MQTTnet 的保活；QoS 0 同样按固定间隔显式确认连接仍可用。
            if (target.Time.GetElapsedTime(_lastPing) >= TimeSpan.FromMilliseconds(target.Settings.PingIntervalMs))
            {
                await _client.PingAsync(operation.Token);
                _lastPing = target.Time.GetTimestamp();
            }

            if ((target.Credential()?.Version ?? "anonymous") != version)
            {
                Reset();
                return new(OutputSendStatus.Retry, "凭证已变化，使用当前凭证重新连接后发送。");
            }

            var result = await _client.PublishAsync(new MqttApplicationMessageBuilder()
                .WithTopic(message.Address).WithPayload(message.Payload.ToArray())
                .WithQualityOfServiceLevel(message.QualityOfService == 1 ? MqttQualityOfServiceLevel.AtLeastOnce : MqttQualityOfServiceLevel.AtMostOnce)
                .WithRetainFlag(false).Build(), operation.Token);
            return result.IsSuccess ? new(OutputSendStatus.Ack) : new(OutputSendStatus.Retry, "MQTT 发布未获得成功确认。");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Reset();
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Reset();
            var suspend = HasAuthenticationFailure(exception);
            return new(suspend ? OutputSendStatus.SuspendTarget : OutputSendStatus.Retry,
                suspend ? "MQTT 认证或 TLS 验证失败，目标暂停。" : "MQTT 网络中断或操作超时，记录保持待发。");
        }
    }

    private static bool HasAuthenticationFailure(Exception exception) =>
        exception is AuthenticationException || exception.InnerException is { } inner && HasAuthenticationFailure(inner);

    private void Reset()
    {
        _client?.Dispose();
        _client = null;
        _connectedVersion = null;
    }

    public ValueTask DisposeAsync()
    {
        Reset();
        return ValueTask.CompletedTask;
    }
}
