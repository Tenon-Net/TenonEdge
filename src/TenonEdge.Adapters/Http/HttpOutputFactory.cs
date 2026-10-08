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
    public RuntimeUnitId Id => id;
    public long ConnectionId => settings.ConnectionId;
    public IReadOnlyList<string> Inputs => settings.Inputs;
    public string? ClientId => null;
    public string Recipient => settings.Endpoint.AbsoluteUri;
    public string CredentialVersion => credentials.DescribeHttp(ConnectionId)?.Version ?? "anonymous";
    // 16 KiB 静态头的 JSON 转义暂存、字典节点与幂等编号额外保守预约。
    public long FormattingReservationBytes => 2L * StandardEnvelope.MaximumPayloadBytes + 128 * 1024;
    public string CurrentFingerprint => settings.Fingerprint(credentials.DescribeHttp(ConnectionId));

    public OutputMessage CreateMessage(EdgeMessage message, Guid deliveryId)
    {
        var identity = credentials.DescribeHttp(ConnectionId);
        if (identity is { Kind: HttpCredentialKind.ApiKeyHeader, Name: { } name } && settings.Headers.ContainsKey(name))
        { throw new ArgumentException("API Key 请求头不能同时保存在非秘密静态请求头中。"); }
        var headers = settings.Headers.SetItem("Idempotency-Key", deliveryId.ToString("D"));
        return new(StandardEnvelope.Serialize(message, deliveryId), settings.Path, null, settings.Fingerprint(identity),
            $"{settings.BaseAddress}; authentication={identity?.Kind.ToString() ?? "none"}", headers);
    }

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
