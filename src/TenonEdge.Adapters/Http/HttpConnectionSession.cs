using System.Net.Http.Headers;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;

namespace TenonEdge.Adapters.Http;

/// <summary>由共享连接管理拥有的 HTTP 客户端；请求路径、凭证及成功约定逐次提供，不绑死某个输出节点。</summary>
internal sealed class HttpConnectionSession : IAsyncDisposable
{
    private readonly HttpClient _client;
    public HttpConnectionSession(IOutboundConnector connector, int timeoutMs) =>
        _client = OutboundHttpTransport.CreateClient(connector, TimeSpan.FromMilliseconds(timeoutMs));

    public async Task<OutputSendResult> SendOutputAsync(HttpOutputSettings settings, IHttpCredentialResolver credentials, TimeProvider time, OutputMessage message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var identity = credentials.DescribeHttp(settings.ConnectionId);
            var credential = identity is null ? null : credentials.ResolveHttp(settings.ConnectionId, settings.BaseAddress);
            if (identity is not null && credential is null)
            { return new(OutputSendStatus.SuspendTarget, "HTTP 凭证未设置或无法解密。"); }
            if (credential is not null && credential.Identity != identity)
            { return new(OutputSendStatus.Retry, "HTTP 凭证正在变化，将重新读取后发送。"); }
            if (message.Fingerprint != settings.Fingerprint(identity))
            { return new(OutputSendStatus.SuspendTarget, "HTTP 接收方身份已变化，旧记录未发送。", FingerprintMismatch: true); }
            var endpoint = HttpOutputSettings.ResolvePath(settings.BaseUri, message.Address);
            HttpSuccessRule? successRule;
            if (message.ProtocolMetadata is { } metadata)
            {
                try
                { successRule = HttpFrozenRequest.Read(metadata, message.Address); }
                catch (Exception exception) when (exception is ArgumentException or JsonException)
                { return new(OutputSendStatus.RejectMessage, "HTTP 冻结请求约定无效，记录未发送。"); }
            }
            else
            {
                // 旧记录没有逐请求约定，不能猜测另一条路径的成功规则。
                if (endpoint != settings.Endpoint)
                { return new(OutputSendStatus.SuspendTarget, "HTTP 旧记录缺少冻结请求约定且路径不匹配，未发送。", FingerprintMismatch: true); }
                return new(OutputSendStatus.SuspendTarget, "HTTP 旧记录缺少冻结成功约定，须核查并清理或迁移，未发送。");
            }
            if (message.Payload.IsEmpty || message.Payload.Length > StandardEnvelope.MaximumPayloadBytes || message.QualityOfService is not null)
            { return new(OutputSendStatus.RejectMessage, "HTTP 冻结报文无效。"); }
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            request.Content = new ReadOnlyMemoryContent(message.Payload);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            if (message.Headers is null || !message.Headers.TryGetValue("Idempotency-Key", out var key) || !Guid.TryParseExact(key, "D", out _))
            { return new(OutputSendStatus.RejectMessage, "HTTP 冻结记录缺少有效的幂等编号。"); }
            foreach (var pair in message.Headers)
            {
                if (!pair.Key.Equals("Idempotency-Key", StringComparison.OrdinalIgnoreCase))
                { HttpOutputSettings.ValidateHeader(pair.Key, pair.Value); }
                request.Headers.Add(pair.Key, pair.Value);
            }
            if (credential is not null)
            {
                try
                { ApplyCredential(request, credential); }
                catch (Exception exception) when (exception is ArgumentException or FormatException)
                { return new(OutputSendStatus.SuspendTarget, "HTTP 凭证格式无效或与固定请求参数冲突。"); }
            }
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var evaluator = new HttpOutputResponse(successRule);
            return await evaluator.EvaluateAsync(response, time.GetUtcNow(), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (HasCause<OutboundAccessDeniedException>(exception) || HasCause<AuthenticationException>(exception))
            { return new(OutputSendStatus.SuspendTarget, "HTTP 出站策略或 TLS 验证失败。"); }
            var network = exception is HttpRequestException or IOException or OperationCanceledException or TimeoutException;
            return new(OutputSendStatus.Retry, network ? "HTTP 网络失败或超时，记录保持待发。" : "HTTP 本地配置或未知异常，记录保持待发。", CountsTowardPoison: !network);
        }
    }

    public async Task<JsonDocument> ReadInputAsync(HttpInputSettings settings, IHttpCredentialResolver credentials, CancellationToken cancellationToken)
    {
        var identity = credentials.DescribeHttp(settings.ConnectionId);
        var credential = identity is null ? null : credentials.ResolveHttp(settings.ConnectionId, settings.BaseAddress);
        if (identity is not null && (credential is null || credential.Identity != identity))
        { throw new InvalidOperationException("HTTP 凭证不可用或正在变化。"); }
        using var request = new HttpRequestMessage(HttpMethod.Get, settings.Endpoint);
        foreach (var pair in settings.Headers)
        { request.Headers.Add(pair.Key, pair.Value); }
        if (credential is not null)
        { ApplyCredential(request, credential); }
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        return await HttpInputResponse.ReadAsync(response, cancellationToken);
    }

    private static void ApplyCredential(HttpRequestMessage request, HttpCredential credential)
    {
        var identity = credential.Identity;
        HttpCredentialValidation.Validate(identity.Kind, identity.Username, identity.Name, credential.Secret);
        switch (identity.Kind)
        {
            case HttpCredentialKind.Basic:
                if (identity.Username is null || identity.Username.IndexOfAny([':', '\r', '\n', '\0']) >= 0)
                { throw new ArgumentException("HTTP Basic 用户名无效。"); }
                request.Headers.Authorization = new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(identity.Username + ":" + credential.Secret)));
                break;
            case HttpCredentialKind.Bearer:
                request.Headers.Authorization = new("Bearer", credential.Secret);
                break;
            case HttpCredentialKind.ApiKeyHeader:
                HttpOutputSettings.ValidateHeader(identity.Name!, credential.Secret, credential: true);
                if (request.Headers.Contains(identity.Name!))
                { throw new ArgumentException("HTTP API Key 请求头冲突。"); }
                request.Headers.Add(identity.Name!, credential.Secret);
                break;
            case HttpCredentialKind.ApiKeyQuery:
                if (string.IsNullOrWhiteSpace(identity.Name) || identity.Name.Length > 128)
                { throw new ArgumentException("HTTP API Key 查询名称无效。"); }
                var endpoint = request.RequestUri!;
                if (endpoint.Query.TrimStart('?').Split('&').Any(part => Uri.UnescapeDataString(part.Split('=')[0].Replace('+', ' ')) == identity.Name))
                { throw new ArgumentException("HTTP API Key 查询参数冲突。"); }
                request.RequestUri = new Uri(endpoint.AbsoluteUri + (endpoint.Query.Length == 0 ? "?" : "&") +
                    Uri.EscapeDataString(identity.Name) + "=" + Uri.EscapeDataString(credential.Secret));
                break;
            default:
                throw new ArgumentException("HTTP 凭证类型无效。");
        }
    }

    private static bool HasCause<T>(Exception exception) where T : Exception => exception is T || exception.InnerException is { } inner && HasCause<T>(inner);
    public ValueTask DisposeAsync() { _client.Dispose(); return ValueTask.CompletedTask; }
}
