using TenonEdge.Abstractions.Contracts;
using TenonEdge.Adapters.Diagnostics;

namespace TenonEdge.Adapters.Http;

/// <summary>
/// 通用 HTTP 输入/输出共用的传输创建入口（T2-01）。URI 的原主机名仍用于 Host/SNI 和证书校验；
/// 连接回调只交还通过实际 IP 校验的 TCP 流。客户端由共享会话拥有，调用方须释放响应。
/// </summary>
public static class OutboundHttpTransport
{
    // clientCertificate 由调用者持有，须晚于 HttpClient 释放；不会被作为服务器信任锚。
    public static HttpClient CreateClient(IOutboundConnector connector, TimeSpan timeout, TlsTrustSnapshot? trust = null,
        TlsClientCertificate? clientCertificate = null)
    {
        ArgumentNullException.ThrowIfNull(connector);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        var handler = CreateHandler(connector);
        HttpMessageHandler pipeline = handler;
        if (trust is not null)
        {
            handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, chain, errors) =>
            ServerCertificateTrust.Evaluate(certificate, chain, errors, trust) is null;
        }
        try
        {
            if (clientCertificate is not null)
            {
                handler.SslOptions.ClientCertificateContext = clientCertificate.Context(DateTimeOffset.UtcNow);
                pipeline = new ClientCertificateHandler(handler, clientCertificate);
            }
            return new HttpClient(pipeline, disposeHandler: true) { Timeout = timeout };
        }
        catch { pipeline.Dispose(); throw; }
    }

    internal static SocketsHttpHandler CreateHandler(IOutboundConnector connector) => new()
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        UseCookies = false,
        // 系统根与连接级信任均只在本地建链；证书内 URL 不能绕过出站连接器。
        SslOptions = new() { CertificateChainPolicy = ServerCertificateTrust.OfflineChainPolicy() },
        // 达到读取上限或无需正文时直接关闭流，不为复用连接在 Dispose 后继续排空未读取的正文。
        MaxResponseDrainSize = 0,
        ConnectCallback = (context, token) => connector.ConnectAsync(context.DnsEndPoint.Host, context.DnsEndPoint.Port, token),
    };

    private sealed class ClientCertificateHandler(HttpMessageHandler inner, TlsClientCertificate certificate) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Validate(request);
            return base.SendAsync(request, cancellationToken);
        }

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Validate(request);
            return base.Send(request, cancellationToken);
        }

        private void Validate(HttpRequestMessage request)
        {
            if (request.RequestUri?.Scheme != "https")
            { throw new ArgumentException("客户端证书认证要求 HTTPS，不能降级为明文请求。"); }
            certificate.ValidateForUse(DateTimeOffset.UtcNow);
        }
    }
}
