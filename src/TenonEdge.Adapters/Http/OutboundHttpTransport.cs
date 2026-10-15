using TenonEdge.Abstractions.Contracts;
using TenonEdge.Adapters.Diagnostics;

namespace TenonEdge.Adapters.Http;

/// <summary>
/// 通用 HTTP 输入/输出共用的传输创建入口（T2-01）。URI 的原主机名仍用于 Host/SNI 和证书校验；
/// 连接回调只交还通过实际 IP 校验的 TCP 流。客户端由共享会话拥有，调用方须释放响应。
/// </summary>
public static class OutboundHttpTransport
{
    public static HttpClient CreateClient(IOutboundConnector connector, TimeSpan timeout, TlsTrustSnapshot? trust = null)
    {
        ArgumentNullException.ThrowIfNull(connector);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        var handler = CreateHandler(connector);
        if (trust is not null)
        {
            handler.SslOptions.CertificateChainPolicy = ServerCertificateTrust.OfflineChainPolicy();
            handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, chain, errors) =>
            ServerCertificateTrust.Evaluate(certificate, chain, errors, trust) is null;
        }
        try
        { return new HttpClient(handler, disposeHandler: true) { Timeout = timeout }; }
        catch { handler.Dispose(); throw; }
    }

    internal static SocketsHttpHandler CreateHandler(IOutboundConnector connector) => new()
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        UseCookies = false,
        // 达到读取上限或无需正文时直接关闭流，不为复用连接在 Dispose 后继续排空未读取的正文。
        MaxResponseDrainSize = 0,
        ConnectCallback = (context, token) => connector.ConnectAsync(context.DnsEndPoint.Host, context.DnsEndPoint.Port, token),
    };
}
