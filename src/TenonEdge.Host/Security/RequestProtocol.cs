namespace TenonEdge.Host.Security;

/// <summary>
/// 请求是否属于 HTTPS（总设计 §10.3）。T0-08 起由请求协议判定中间件按“受信任代理只看转发头、其他请求看到达的监听端口”写入；
/// 没有判定结果时（例如进程内测试）按连接本身判断。Cookie 名称与 Secure 标志、会话协议校验都以此为准。
/// </summary>
internal static class RequestProtocol
{
    public static bool IsHttps(HttpContext context) =>
        context.Features.Get<IRequestProtocolFeature>()?.IsHttps ?? context.Request.IsHttps;

    public static string Name(HttpContext context) => IsHttps(context) ? Uri.UriSchemeHttps : Uri.UriSchemeHttp;
}

/// <summary>请求协议的判定结果。</summary>
internal interface IRequestProtocolFeature
{
    bool IsHttps { get; }
}
