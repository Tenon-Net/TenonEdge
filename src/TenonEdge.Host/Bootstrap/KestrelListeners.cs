using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;
using TenonEdge.Host.Security;

namespace TenonEdge.Host.Bootstrap;

/// <summary>
/// 按 <see cref="ListenerSettings"/> 配置 Kestrel 的 HTTP 与 HTTPS 监听，HTTPS 使用 <see cref="HttpsCertificateStore"/> 的证书。
/// <para>
/// 作为启动检查的最后一步执行，而不是注册为 KestrelServerOptions 的配置：WebApplication 在构建时就会创建服务器并读取其选项，
/// 那时还没有取得数据目录锁；若在那里取证书，会连带提前打开配置库。服务器在开始运行时才按这里加入的端点监听。
/// 进程内测试使用 TestServer，这里加入的端点不会被使用。
/// </para>
/// </summary>
internal static class KestrelListeners
{
    public const long MaxRequestBodySize = 1024 * 1024;

    public static void Configure(IServiceProvider services)
    {
        var settings = services.GetRequiredService<ListenerSettings>();
        var certificate = services.GetRequiredService<HttpsCertificateStore>().Certificate;
        var options = services.GetRequiredService<IOptions<KestrelServerOptions>>().Value;

        // 管理 API 请求体默认上限 1 MiB，超出返回 413（总设计 §8.7）；个别接口需要更大时在端点上单独声明。
        options.Limits.MaxRequestBodySize = MaxRequestBodySize;

        if (settings.Http is { } http)
        {
            Listen(options, http, _ => { });
        }

        Listen(options, settings.Https, listen => listen.UseHttps(certificate));
    }

    private static void Listen(KestrelServerOptions options, ListenEndpoint endpoint, Action<ListenOptions> configure)
    {
        switch (endpoint.Address.Kind)
        {
            case ListenAddressKind.Any:
                options.ListenAnyIP(endpoint.Port, configure);
                break;
            case ListenAddressKind.Localhost:
                options.ListenLocalhost(endpoint.Port, configure);
                break;
            default:
                options.Listen(endpoint.Address.Ip!, endpoint.Port, configure);
                break;
        }
    }
}
