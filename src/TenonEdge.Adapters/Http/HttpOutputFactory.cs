using System.Collections.Immutable;
using System.Text.Json;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Abstractions.Plans;

namespace TenonEdge.Adapters.Http;

public sealed class HttpOutputFactory(IHttpCredentialResolver credentials, TimeProvider time, IOutboundConnector connector, ISharedConnections connections) : IOutputAdapterFactory
{
    public string Kind => "output.http";
    public IOutputTarget Build(RuntimeUnitPlan plan)
    {
        try
        { return new HttpOutputTarget(plan.Id, HttpOutputSettings.Parse(plan.Configuration), credentials, time, connector, connections); }
        catch (Exception exception) when (exception is ArgumentException or JsonException or FormatException or InvalidOperationException)
        { throw new RuntimeUnitBuildException(plan.Id, "HTTP 输出配置无效，请检查固定地址、请求头和成功约定。", exception); }
    }
}

internal sealed class HttpOutputTarget(RuntimeUnitId id, HttpOutputSettings settings, IHttpCredentialResolver credentials,
    TimeProvider time, IOutboundConnector connector, ISharedConnections connections) : IOutputTarget
{
    private readonly string _frozenRequest = HttpFrozenRequest.Freeze(settings);
    private readonly OutputPayloadFormat _payload = OutputPayloadFormat.Parse(settings.Payload);
    public long MinimumSerializationReservationBytes => Math.Max(_payload.MinimumSerializationReservationBytes, 4L * OutputRecipientIdentity.MaximumSummaryBytes);
    public RuntimeUnitId Id => id;
    public long ConnectionId => settings.ConnectionId;
    public IReadOnlyList<string> Inputs => settings.Inputs;
    public string? ClientId => null;
    public string Recipient => settings.BaseAddress;
    public string CredentialVersion => credentials.DescribeHttp(ConnectionId)?.Version ?? "anonymous";
    // 16 KiB 静态头、字典节点、幂等编号和摘要UTF-16结果逐路径保留；摘要编码暂存串行复用。
    public long FormattingReservationBytes => 2L * StandardEnvelope.MaximumPayloadBytes + 128 * 1024 +
        2L * OutputMessage.MaximumProtocolMetadataBytes + 2L * OutputRecipientIdentity.MaximumSummaryBytes;
    public string CurrentFingerprint => settings.Fingerprint(credentials.DescribeHttp(ConnectionId));
    public TimeSpan SendTimeout => TimeSpan.FromMilliseconds(settings.TimeoutMs);
    public OutputTargetIdentity CaptureIdentity() => CaptureIdentity(credentials.DescribeHttp(ConnectionId));
    private OutputTargetIdentity CaptureIdentity(HttpCredentialIdentity? identity) =>
        new(settings.Fingerprint(identity), OutputRecipientIdentity.Http(Recipient, identity));

    public OutputMessage CreateMessage(EdgeMessage message, Guid deliveryId)
    {
        var identity = credentials.DescribeHttp(ConnectionId);
        if (identity is { Kind: HttpCredentialKind.ApiKeyHeader, Name: { } name } && settings.Headers.ContainsKey(name))
        { throw new ArgumentException("API Key 请求头不能同时保存在非秘密静态请求头中。"); }
        var headers = settings.Headers.SetItem("Idempotency-Key", deliveryId.ToString("D"));
        var recipient = CaptureIdentity(identity);
        return new(_payload.Serialize(message, deliveryId), settings.Path, null, recipient.Fingerprint,
            recipient.Summary, headers, _frozenRequest);
    }

    private HttpOutputSettings Settings => settings;
    public bool CanShareSenderWith(IOutputTarget other) => other is HttpOutputTarget http &&
        ConnectionId == http.ConnectionId && settings.BaseAddress == http.Settings.BaseAddress && settings.TimeoutMs == http.Settings.TimeoutMs;

    public IOutputAdapter CreateAdapter()
    {
        // 会话只按连接参数共享；路径、静态头和业务成功规则属于每次请求，秘密与轮换版本也不创建第二个客户端。
        var key = JsonSerializer.Serialize(new { settings.BaseAddress, settings.TimeoutMs });
        var lease = connections.Acquire(ConnectionId, "http", key, () => new HttpConnectionSession(connector, settings.TimeoutMs));
        return new HttpOutputAdapter(lease, settings, credentials, time);
    }
}

internal sealed class HttpOutputAdapter(IConnectionLease<HttpConnectionSession> connection, HttpOutputSettings settings, IHttpCredentialResolver credentials, TimeProvider time) : IOutputAdapter
{
    public async Task<OutputSendResult> SendAsync(OutputMessage message, CancellationToken cancellationToken)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(settings.TimeoutMs), time);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, cancellationToken);
        try
        { return await connection.ExecuteAsync((session, token) => session.SendOutputAsync(settings, credentials, time, message, token), operation.Token); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return new(OutputSendStatus.Retry, "HTTP 排队或网络操作超时，记录保持待发。"); }
    }
    public ValueTask DisposeAsync() => connection.DisposeAsync();
}
