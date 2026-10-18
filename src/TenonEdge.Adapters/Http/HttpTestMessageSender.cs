using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;

namespace TenonEdge.Adapters.Http;

/// <summary>状态码表示已经收到 HTTP 响应，不证明对方业务落库。正文仅可按配置人员/管理员正文权限展示，不持久化或写日志。</summary>
public sealed record HttpTestSendResult(string Code, int? StatusCode, HttpResponseSummaryState? BodyState,
    string? Body, bool Truncated, double ElapsedMs)
{
    public override string ToString() => $"HTTP test send ({Code}, {StatusCode}, {BodyState}; body omitted)";
}

/// <summary>一次显式 POST 的传输原语：借用原 HTTP 串行会话，不入队、不重试、不跟随重定向。
/// Host 须先完成权限、二次确认、凭证接收方绑定和共享预览准入；本类不提供管理入口。</summary>
public sealed class HttpTestMessageSender(ISharedConnections connections, IOutboundConnector connector, TimeProvider time, ITlsTrustResolver? trust = null)
{
    public async Task<HttpTestSendResult> SendAsync(HttpOutputSettings settings, HttpCredential? credential,
        OutputMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(message);
        settings = (HttpOutputSettings)settings.ValidateRequest();
        var endpoint = HttpOutputSettings.ResolvePath(settings.BaseUri, message.Address);
        if (message.ProtocolMetadata is null || message.Payload.IsEmpty || message.Payload.Length > StandardEnvelope.MaximumPayloadBytes ||
            message.QualityOfService is not null || message.Headers is null || message.Headers.Count > 65 ||
            !message.Headers.TryGetValue("Idempotency-Key", out var key) || !Guid.TryParseExact(key, "D", out _) ||
            message.Headers.Where(pair => !pair.Key.Equals("Idempotency-Key", StringComparison.OrdinalIgnoreCase))
                .Sum(pair => Encoding.UTF8.GetByteCount(pair.Key) + Encoding.UTF8.GetByteCount(pair.Value)) > 16384 ||
            message.Headers.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != message.Headers.Count)
        { throw new ArgumentException("HTTP 测试须使用完整、有效的冻结输出报文。"); }
        _ = HttpFrozenRequest.Read(message.ProtocolMetadata, message.Address);
        using var request = HttpFrozenRequest.CreatePost(endpoint, message);
        if (credential is not null)
        { HttpConnectionSession.ApplyCredential(request, credential); }
        cancellationToken.ThrowIfCancellationRequested();
        var started = time.GetTimestamp();
        if (message.Fingerprint != settings.Fingerprint(credential?.Identity))
        { return Failure("recipient.changed"); }
        // 包含等待共享串行租约、建连、发送及有界响应读取的总期限。
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(settings.TimeoutMs), time);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var identity = JsonSerializer.Serialize(new { settings.BaseAddress, settings.TimeoutMs });
        IConnectionLease<HttpConnectionSession> lease;
        try
        { lease = connections.Acquire(settings.ConnectionId, "http", identity, () => new HttpConnectionSession(connector, settings.TimeoutMs, trust, time)); }
        catch (InvalidOperationException) { throw new HttpConnectionUnavailableException(); }
        await using var ownership = lease;
        var entered = false;
        try
        {
            var result = await lease.ExecuteAsync((session, token) =>
            {
                token.ThrowIfCancellationRequested();
                entered = true;
                return session.SendTestOutputAsync(settings, request, credential, token);
            }, operation.Token);
            cancellationToken.ThrowIfCancellationRequested();
            return result with { ElapsedMs = time.GetElapsedTime(started).TotalMilliseconds };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return Failure(entered ? "http.timeoutUnknown" : "connection.busy"); }
        catch (Exception exception) when (exception is HttpRequestException or IOException or SocketException or AuthenticationException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Failure(HasCause<OutboundAccessDeniedException>(exception) ? "outbound.denied" :
                HasCause<AuthenticationException>(exception) ? "certificate.invalid" :
                deadline.IsCancellationRequested ? "http.timeoutUnknown" : "http.deliveryUnknown");
        }

        HttpTestSendResult Failure(string code) => new(code, null, null, null, false, time.GetElapsedTime(started).TotalMilliseconds);
    }

    private static bool HasCause<T>(Exception exception) where T : Exception => exception is T || exception.InnerException is { } inner && HasCause<T>(inner);
}
