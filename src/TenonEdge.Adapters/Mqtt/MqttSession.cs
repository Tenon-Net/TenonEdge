using System.Security.Authentication;
using MQTTnet;
using MQTTnet.Exceptions;
using MQTTnet.Formatter;
using MQTTnet.Protocol;
using TenonEdge.Abstractions.Contracts;

namespace TenonEdge.Adapters.Mqtt;

/// <summary>由运行时共享连接管理拥有客户端，经借用的串行执行器调用，无回调重连。所有外部 I/O 有显式期限；异常文本不向运行事件泄露秘密。</summary>
internal sealed class MqttSession(MqttConnectionSettings settings, string clientId, ICredentialResolver credentials, TimeProvider time, IOutboundConnector connector, MqttInputReceiver? input = null,
    Func<CancellationToken, Task>? waitForActivation = null) : IOutputAdapter
{
    private IMqttClient? _client;
    private MqttReceiveBuffer? _receiveBuffer;
    private readonly CancellationTokenSource _outboundStop = new();
    private string? _connectedVersion;
    private long _lastPing;
    private CancellationTokenSource? _receiveCancellation;
    private int _inputFailed;
    private readonly Lock _receiveLock = new();
    internal bool IsConnected => Volatile.Read(ref _inputFailed) == 0 && _client?.IsConnected == true;

    public Task<OutputSendResult> SendAsync(OutputMessage message, CancellationToken cancellationToken) => ExecuteAsync(message, cancellationToken);

    /// <summary>输入拥有者经共享执行器定期调用；连接/订阅/保活有调用方提供的总期限，不从回调重连。</summary>
    public Task<OutputSendResult> MaintainAsync(CancellationToken cancellationToken) => ExecuteAsync(null, cancellationToken);

    internal Task CancelOutboundForDrainAsync() => _outboundStop.CancelAsync();
    internal void DisconnectAfterDrain() => Reset();

    private async Task<OutputSendResult> ExecuteAsync(OutputMessage? message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (input?.IsQuiescing == true)
        { return new(OutputSendStatus.Retry, "MQTT 输入连接正在停止，记录保持待发。"); }
        using var outbound = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _outboundStop.Token);
        cancellationToken = outbound.Token;
        // 所有触发连接的路径共用屏障，包括先启动的输出；等待不创建客户端或解析凭证。
        // 委托由计划/连接绑定协调者提供，调用方期限覆盖等待，不能取消其他借用者的屏障。
        if (waitForActivation is not null)
        { await waitForActivation(cancellationToken); }
        cancellationToken.ThrowIfCancellationRequested();
        if (Volatile.Read(ref _inputFailed) != 0)
        { Reset(); }
        var credential = Credential();
        if (settings.UsePassword && credential is null)
        {
            Reset();
            return new(OutputSendStatus.SuspendTarget, "MQTT 凭证未设置、已作废或无法解密。");
        }

        if (message is not null && !string.Equals(message.Fingerprint, settings.Fingerprint(credential?.Username, credential?.AccountMarker), StringComparison.Ordinal))
        {
            Reset();
            return new(OutputSendStatus.SuspendTarget, "接收方身份已变化，已停止发送旧记录。", FingerprintMismatch: true);
        }

        try
        {
            var version = credential?.Version ?? "anonymous";
            if (_connectedVersion != version)
            {
                Reset();
            }

            if (_client?.IsConnected != true)
            {
                // IsConnected=false 也可能仍在异步 DisconnectCore 中；旧对象不能再次 Connect。
                // 用新的客户端代次重连，保留接收器中的持久订阅状态和实际 ClientId。
                Reset();
                IMqttClient? client = null;
                Func<MqttApplicationMessageReceivedEventArgs, CancellationToken, Task>? receive = null;
                if (input is not null)
                {
                    var generation = new CancellationTokenSource();
                    lock (_receiveLock)
                    { _receiveCancellation = generation; }
                    var receiveToken = generation.Token;
                    receive = async (args, transportToken) =>
                    {
                        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(receiveToken, transportToken);
                        await input.ReceiveAsync(args, () =>
                        {
                            lock (_receiveLock)
                            { if (ReferenceEquals(_receiveCancellation, generation)) { Volatile.Write(ref _inputFailed, 1); } }
                            try
                            { generation.Cancel(); }
                            catch (ObjectDisposedException) { }
                            client!.Dispose();
                        }, lifetime.Token);
                    };
                    if (input.BufferBudget is not null)
                    { _receiveBuffer = new MqttReceiveBuffer(input.BufferBudget, 100, receive, input.ReportBuffer, time); }
                }
                client = new MqttClientFactory().CreateMqttClient(new MqttBoundedAdapterFactory(
                    () => input?.RecordWireOversized(), admit: input is null ? AdmitOutputFrame : null, receiveBuffer: _receiveBuffer));
                _client = client;
                if (_receiveBuffer is null && receive is not null)
                { client.ApplicationMessageReceivedAsync += args => receive(args, CancellationToken.None); }
                var builder = new MqttClientOptionsBuilder()
                    .WithTcpServer(settings.Host, settings.Port)
                    .WithStreamProvider(new MqttStreamProvider(connector))
                    .WithProtocolVersion(MqttProtocolVersion.V311)
                    .WithCleanSession(input is null)
                    .WithClientId(clientId)
                    .WithKeepAlivePeriod(TimeSpan.FromMilliseconds(settings.PingIntervalMs))
                    .WithTimeout(TimeSpan.FromMilliseconds(settings.TimeoutMs));
                if (credential is not null)
                {
                    builder.WithCredentials(credential.Username, credential.Password);
                }

                if (settings.Tls)
                {
                    builder.WithTlsOptions(tls => tls.UseTls());
                }

                var connected = await _client.ConnectAsync(builder.Build(), cancellationToken);
                if (connected.ResultCode != MqttClientConnectResultCode.Success)
                {
                    Reset();
                    return new(connected.ResultCode == MqttClientConnectResultCode.ServerUnavailable ? OutputSendStatus.Retry : OutputSendStatus.SuspendTarget, "MQTT Broker 拒绝连接，请检查身份或协议配置。");
                }

                if (input is not null)
                { await input.SubscribeAsync(_client, connected.IsSessionPresent, cancellationToken); }
                _connectedVersion = version;
                _lastPing = time.GetTimestamp();
            }

            // 持续发送会抑制 MQTTnet 的保活；QoS 0 同样按固定间隔显式确认连接仍可用。
            if (time.GetElapsedTime(_lastPing) >= TimeSpan.FromMilliseconds(settings.PingIntervalMs))
            {
                await _client.PingAsync(cancellationToken);
                _lastPing = time.GetTimestamp();
            }

            if ((Credential()?.Version ?? "anonymous") != version)
            {
                Reset();
                return new(OutputSendStatus.Retry, "凭证已变化，使用当前凭证重新连接后发送。");
            }

            if (message is null)
            { return new(OutputSendStatus.Ack); }
            var result = await _client.PublishAsync(new MqttApplicationMessageBuilder()
                .WithTopic(message.Address).WithPayload(message.Payload.ToArray())
                .WithQualityOfServiceLevel(message.QualityOfService == 1 ? MqttQualityOfServiceLevel.AtLeastOnce : MqttQualityOfServiceLevel.AtMostOnce)
                .WithRetainFlag(false).Build(), cancellationToken);
            return result.IsSuccess ? new(OutputSendStatus.Ack) : new(OutputSendStatus.Retry, "MQTT 发布未获得成功确认。", CountsTowardPoison: true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 静默中的输出/维护取消不能抢先取消仍在等待存储提交的接收代次。
            if (input?.IsQuiescing != true)
            { Reset(); }
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var inputFailure = Volatile.Read(ref _inputFailed) != 0 || input?.IsQuiescing == true;
            if (input?.IsQuiescing != true)
            { Reset(); }
            if (HasAccessDenial(exception))
            {
                return new(OutputSendStatus.SuspendTarget, "目标被出站访问规则拦截，请联系管理员。");
            }
            var suspend = HasAuthenticationFailure(exception);
            var transient = inputFailure || exception is MqttCommunicationException or OperationCanceledException or TimeoutException
                or IOException or System.Net.Sockets.SocketException;
            return new(suspend ? OutputSendStatus.SuspendTarget : OutputSendStatus.Retry,
                suspend ? "MQTT 认证或 TLS 验证失败，目标暂停。"
                    : transient ? "MQTT 网络中断或操作超时，记录保持待发。" : "MQTT 本地未知异常或无法识别的响应。",
                CountsTowardPoison: !suspend && !transient);
        }
    }

    private static bool AdmitOutputFrame(byte header, int remainingLength)
    {
        // 输出专用会话使用 CleanSession 且从不订阅；不要让意外 PUBLISH 进入库的无界队列。
        if ((header >> 4) == 3)
        { throw new MqttCommunicationException("未配置 MQTT 输入的连接收到 PUBLISH，已关闭连接。"); }
        return true;
    }

    private MqttCredential? Credential() => settings.UsePassword ? credentials.ResolveMqtt(settings.ConnectionId, settings.Recipient) : null;

    private static bool HasAccessDenial(Exception exception) =>
        exception is OutboundAccessDeniedException || exception.InnerException is { } inner && HasAccessDenial(inner);

    private static bool HasAuthenticationFailure(Exception exception) =>
        exception is AuthenticationException || exception.InnerException is { } inner && HasAuthenticationFailure(inner);

    private void Reset()
    {
        CancellationTokenSource? generation;
        lock (_receiveLock)
        {
            generation = _receiveCancellation;
            _receiveCancellation = null;
            Volatile.Write(ref _inputFailed, 0);
        }
        if (generation is not null)
        {
            generation.Cancel();
            generation.Dispose();
        }
        _client?.Dispose();
        _client = null;
        _receiveBuffer?.Dispose();
        _receiveBuffer = null;
        _connectedVersion = null;
    }

    public ValueTask DisposeAsync()
    {
        Reset();
        _outboundStop.Dispose();
        return ValueTask.CompletedTask;
    }
}
