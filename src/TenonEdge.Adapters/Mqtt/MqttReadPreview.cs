using System.Buffers;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json;
using System.Text.Unicode;
using MQTTnet;
using MQTTnet.Exceptions;
using MQTTnet.Formatter;
using MQTTnet.Protocol;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Adapters.Diagnostics;

namespace TenonEdge.Adapters.Mqtt;

public sealed record MqttReadPreviewResult(string Code, string? Topic, bool TopicOmitted, bool Retained, bool Duplicate, int? Qos,
    double ElapsedMs, JsonReadPreviewResult? Preview)
{
    public override string ToString() => $"MQTT read preview ({Code}; topic and payload omitted)";
}

public sealed record MqttReadSampleResult(string Code, bool Retained, bool Duplicate, int? Qos,
    EdgeMessage? Message, bool InvalidUpstreamTime)
{
    public override string ToString() => $"MQTT input sample ({Code}; payload omitted)";
}

/// <summary>管理器拥有的独立随机 ClientId/CleanSession 诊断订阅，不改变正式订阅或确认状态。
/// 只等待一条匹配消息；原始报文在分配前预约共享预算，返回前关闭连接并等回调退出。</summary>
public sealed class MqttReadPreview(ISharedConnections connections, IOutboundConnector connector,
    ICredentialResolver credentials, IJsonInputDecoderFactory decoders, IInputBufferBudget bufferBudget, TimeProvider time, ITlsTrustResolver? trust = null)
{
    public async Task<MqttReadPreviewResult> RunAsync(MqttInputSettings settings, ClockSyncState clockState,
        CancellationToken cancellationToken, int timeoutMs = 30000)
    {
        var captured = await CaptureAsync(settings, clockState, timeoutMs, (root, decoder, secrets, receivedAt, token) =>
        {
            var preview = JsonReadPreview.Render(root, decoder, null, clockState, receivedAt, null,
                token, maximumDisplayBytes: 60 * 1024, additionalSecrets: secrets);
            return (preview.Code, preview);
        }, cancellationToken);
        return new(captured.Code, captured.Topic, captured.TopicOmitted, captured.Retained, captured.Duplicate,
            captured.Qos, captured.ElapsedMs, captured.Payload);
    }

    public async Task<MqttReadSampleResult> ReadSampleAsync(MqttInputSettings settings, string inputKey, string deviceKey,
        ClockSyncState clockState, CancellationToken cancellationToken, int timeoutMs = 30000)
    {
        BusinessKeys.Validate(inputKey, nameof(inputKey));
        BusinessKeys.Validate(deviceKey, nameof(deviceKey));
        if (settings.Fields.ValueKind != JsonValueKind.Array || settings.Fields.GetArrayLength() == 0)
        { throw new ArgumentException("完整路线样本须有正式字段定义；空字段仅用于诊断展示/推断。"); }
        var captured = await CaptureAsync(settings, clockState, timeoutMs, (root, decoder, secrets, receivedAt, token) =>
        {
            var sample = JsonReadSample.Decode(root, decoder!, null, inputKey, deviceKey, clockState, receivedAt, token, secrets);
            return (sample.Code, sample);
        }, cancellationToken);
        return new(captured.Code, captured.Retained, captured.Duplicate, captured.Qos,
            captured.Payload?.Message, captured.Payload?.InvalidUpstreamTime ?? false);
    }

    private sealed record CaptureResult<T>(string Code, string? Topic, bool TopicOmitted, bool Retained, bool Duplicate,
        int? Qos, double ElapsedMs, T? Payload) where T : class;

    private async Task<CaptureResult<T>> CaptureAsync<T>(MqttInputSettings settings, ClockSyncState clockState, int timeoutMs,
        Func<JsonElement, IJsonInputDecoder?, string[], DateTimeOffset, CancellationToken, (string Code, T Payload)> decode,
        CancellationToken cancellationToken) where T : class
    {
        settings = (MqttInputSettings)settings.ValidateConnection();
        MqttInputSettings.ValidateSubscription(settings.TopicFilter, settings.Qos, settings.ProcessRetained, settings.UpstreamTime);
        if (timeoutMs is < 100 or > 30000 || !Enum.IsDefined(clockState) || settings.Fields.ValueKind != JsonValueKind.Array)
        { throw new ArgumentException("MQTT 采样配置无效。"); }
        var decoder = settings.Fields.GetArrayLength() == 0 ? null : decoders.Build(settings.Fields, settings.UpstreamTime);
        cancellationToken.ThrowIfCancellationRequested();
        var started = time.GetTimestamp();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(timeoutMs), time);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        MqttCredential? credential;
        try
        { credential = MqttCredentialValidation.Resolve(settings, credentials); }
        catch (ArgumentException) { return Failure("credential.unavailable"); }
        IConnectionLease<Capture> lease;
        try
        { lease = connections.AcquireDiagnostic("mqtt-preview", () => new Capture()); }
        catch (InvalidOperationException) { return Failure("connection.capacity"); }
        await using var ownership = lease;
        try
        {
            return await lease.ExecuteAsync(async (capture, token) =>
            {
                var snapshot = settings.Tls ? trust?.ResolveTlsTrust(settings.ConnectionId, settings.Recipient) : null;
                var received = new TaskCompletionSource<CaptureResult<T>>(TaskCreationOptions.RunContinuationsAsynchronously);
                var claimed = 0;
                string? wireFailure = null;
                capture.Buffer = new MqttReceiveBuffer(bufferBudget, 1, Receive, reason =>
                {
                    if (reason is "bufferFull" or "bufferFailed")
                    { received.TrySetResult(Failure("buffer.unavailable")); }
                }, time);
                capture.Certificate = MqttCredentialValidation.OpenCertificate(credential, time.GetUtcNow());
                capture.Client = new MqttClientFactory().CreateMqttClient(new MqttBoundedAdapterFactory(
                    () => { Volatile.Write(ref wireFailure, "payload.tooLarge"); }, receiveBuffer: capture.Buffer, clientCertificate: capture.Certificate));
                var client = capture.Client;
                client.DisconnectedAsync += _ =>
                {
                    received.TrySetResult(Failure(Volatile.Read(ref wireFailure) ?? "connection.closed"));
                    return Task.CompletedTask;
                };
                var options = new MqttClientOptionsBuilder().WithTcpServer(settings.Host, settings.Port)
                    .WithStreamProvider(new MqttStreamProvider(connector)).WithProtocolVersion(MqttProtocolVersion.V311)
                    .WithClientId("te-sample-" + Guid.NewGuid().ToString("N")).WithCleanSession()
                    .WithKeepAlivePeriod(TimeSpan.FromMilliseconds(settings.PingIntervalMs))
                    .WithTimeout(TimeSpan.FromMilliseconds(settings.TimeoutMs));
                if (credential?.Kind == MqttCredentialKind.Password)
                { options.WithCredentials(credential.Username, credential.Password); }
                if (settings.Tls)
                {
                    options.WithTlsOptions(tls => tls.UseTls().WithCertificateValidationHandler(args =>
                    ServerCertificateTrust.Evaluate(args.Certificate, args.Chain, args.SslPolicyErrors, snapshot) is null));
                }
                using (var setupDeadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(settings.TimeoutMs), time))
                using (var setup = CancellationTokenSource.CreateLinkedTokenSource(token, setupDeadline.Token))
                {
                    var connected = await client.ConnectAsync(options.Build(), setup.Token);
                    if (connected.ResultCode != MqttClientConnectResultCode.Success)
                    { return Failure(connected.ResultCode == MqttClientConnectResultCode.ServerUnavailable ? "broker.unavailable" : "credential.rejected"); }
                    if (!CredentialUnchanged())
                    { return Failure("credential.changed"); }
                    var subscribed = await client.SubscribeAsync(new MqttClientSubscribeOptionsBuilder()
                        .WithTopicFilter(settings.TopicFilter, (MqttQualityOfServiceLevel)settings.Qos).Build(), setup.Token);
                    if (subscribed.Items.Count != 1 || (int)subscribed.Items.Single().ResultCode > 1)
                    { return Failure("subscription.rejected"); }
                }
                var result = await received.Task.WaitAsync(token);
                token.ThrowIfCancellationRequested();
                return CredentialUnchanged() ? result : Failure("credential.changed");

                bool CredentialUnchanged() => (!settings.Tls || trust?.ResolveTlsTrust(settings.ConnectionId, settings.Recipient) == snapshot) &&
                    (credential is not null ? credentials.ResolveMqtt(settings.ConnectionId, settings.Recipient)?.Identity == credential.Identity
                        : credentials.DescribeMqtt(settings.ConnectionId) is null);

                async Task Receive(MqttApplicationMessageReceivedEventArgs args, CancellationToken lifetime)
                {
                    args.AutoAcknowledge = false;
                    using var callback = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime);
                    var message = args.ApplicationMessage;
                    if (MqttTopicFilterComparer.Compare(message.Topic, settings.TopicFilter) != MqttTopicFilterCompareResult.IsMatch ||
                        Interlocked.CompareExchange(ref claimed, 1, 0) != 0)
                    { return; }
                    CaptureResult<T> result;
                    var secrets = credential is null ? Array.Empty<string>() : new[] { credential.Password, Uri.EscapeDataString(credential.Password) };
                    try
                    {
                        if (message.Payload.Length > MqttInputReceiver.MaximumPayloadBytes)
                        { result = Failure("payload.tooLarge"); }
                        else
                        {
                            var bytes = message.Payload.ToArray();
                            if (!Utf8.IsValid(bytes))
                            { throw new JsonException(); }
                            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64 });
                            var decoded = decode(document.RootElement, decoder, secrets, time.GetUtcNow(), callback.Token);
                            // 主题也可能含秘密；不允许用主题绕过正文脱敏，也限制最长 Topic 的展示。
                            var topic = message.Topic;
                            var safeTopic = secrets.Any(secret => secret.Length > 0 && topic.Contains(secret, StringComparison.Ordinal))
                                ? null : topic.Length <= 256 ? topic : null;
                            result = new(decoded.Code, safeTopic, safeTopic is null, message.Retain, message.Dup, (int)message.QualityOfServiceLevel,
                                time.GetElapsedTime(started).TotalMilliseconds, decoded.Payload);
                        }
                    }
                    catch (JsonException) { result = Failure("payload.invalidJson"); }
                    catch (InputMessageSizeException) { result = Failure("fields.tooLarge"); }
                    callback.Token.ThrowIfCancellationRequested();
                    // 此 PUBACK 只属于随机 CleanSession 采样客户端，不是正式采集的持久接管确认。
                    if (message.QualityOfServiceLevel == MqttQualityOfServiceLevel.AtLeastOnce)
                    { await args.AcknowledgeAsync(callback.Token); }
                    received.TrySetResult(result);
                }
            }, operation.Token);
        }
        catch (ArgumentException) { return Failure("credential.unavailable"); }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or SocketException or AuthenticationException or MqttCommunicationException or MqttProtocolViolationException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Failure(deadline.IsCancellationRequested ? "timeout" : Classify(exception));
        }

        CaptureResult<T> Failure(string code) => new(code, null, false, false, false, null, time.GetElapsedTime(started).TotalMilliseconds, null);
    }

    private static string Classify(Exception exception) => exception switch
    {
        OutboundAccessDeniedException => "outbound.denied",
        AuthenticationException => "certificate.invalid",
        MqttProtocolViolationException => "protocol.invalid",
        OperationCanceledException or MqttCommunicationTimedOutException => "timeout",
        SocketException socket when socket.SocketErrorCode is SocketError.HostNotFound or SocketError.NoData => "dns.failed",
        SocketException socket when socket.SocketErrorCode == SocketError.ConnectionRefused => "connection.refused",
        _ => exception.InnerException is { } inner ? Classify(inner) : "connection.failed"
    };

    private sealed class Capture : IAsyncDisposable
    {
        internal TlsClientCertificate? Certificate { get; set; }
        internal IMqttClient? Client { get; set; }
        internal MqttReceiveBuffer? Buffer { get; set; }
        public async ValueTask DisposeAsync()
        {
            try
            {
                Client?.Dispose();
                Buffer?.Dispose();
                if (Buffer is not null)
                { await Buffer.Completion; }
            }
            finally { Certificate?.Dispose(); }
        }
    }
}
