using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using TenonEdge.Host.Features.SystemInfo;

namespace TenonEdge.Host.Security;

/// <summary>
/// 请求协议判定与转发头处理（总设计 §10.3）：
/// <list type="bullet">
/// <item>来自受信任代理的请求：只看转发头中的协议（X-Forwarded-Proto 的最后一个值），没有转发头按 HTTP 处理，与到达哪个监听端口无关；
/// 其他请求：按到达的监听端口（连接本身是否为 TLS）判定。结果写入 <see cref="IRequestProtocolFeature"/>，Cookie 与会话校验以此为准。</item>
/// <item>只有登记了受信任代理时才启用转发头中间件，且只信任所列地址、只处理 X-Forwarded-For，转发一层；来源地址随之取代理转发的客户端地址。</item>
/// <item>不使用 ASPNETCORE_FORWARDEDHEADERS_ENABLED：它会清空信任列表、接受任何来源的转发头。框架据此注册的启动过滤器在注册服务时移除，
/// 设置了该变量时启动日志给出警告。</item>
/// </list>
/// 401 响应补上 WWW-Authenticate 质询头（RFC 9110 §15.5.2）：管理会话使用 Cookie 方案。
/// </summary>
internal static partial class RequestSecurityPipeline
{
    public const string SessionChallenge = "Cookie realm=\"TenonEdge\"";

    private const string ForwardedHeadersEnabledKey = "ForwardedHeaders_Enabled";

    public static IServiceCollection AddRequestSecurity(this IServiceCollection services)
    {
        // 框架在 ASPNETCORE_FORWARDEDHEADERS_ENABLED=true 时注册 ForwardedHeadersStartupFilter；移除它，转发头只按登记的代理处理。
        var filters = services
            .Where(descriptor => descriptor.ServiceType == typeof(IStartupFilter)
                && descriptor.ImplementationType?.Name == "ForwardedHeadersStartupFilter")
            .ToList();
        foreach (var descriptor in filters)
        {
            services.Remove(descriptor);
        }

        services.AddSingleton(provider => TrustedProxies.Resolve(provider.GetRequiredService<IConfiguration>()));
        return services;
    }

    public static WebApplication UseRequestSecurity(this WebApplication app)
    {
        var rules = app.Services.GetRequiredService<TrustedProxyStore>();
        if (string.Equals(app.Configuration[ForwardedHeadersEnabledKey], "true", StringComparison.OrdinalIgnoreCase))
        {
            LogForwardedHeadersVariableIgnored(app.Logger);
        }

        app.Use(next =>
        {
            var gate = new Lock();
            PreparedProxyPipeline? cached = null;
            return async context =>
            {
                var snapshot = rules.Current;
                PreparedProxyPipeline prepared;
                lock (gate)
                {
                    if (cached is null || !ReferenceEquals(cached.Snapshot, snapshot))
                    { cached = Prepare(snapshot, next, app.Services.GetRequiredService<ILoggerFactory>()); }
                    prepared = cached;
                }
                // Capture one policy before forwarding can replace RemoteIpAddress.
                context.Features.Set<IRequestProtocolFeature>(new RequestProtocolFeature(IsHttps(context, snapshot.Proxies)));
                context.Response.OnStarting(() =>
                {
                    if (context.Response.StatusCode == StatusCodes.Status401Unauthorized
                        && !context.Response.Headers.ContainsKey(HeaderNames.WWWAuthenticate))
                    { context.Response.Headers.WWWAuthenticate = SessionChallenge; }
                    return Task.CompletedTask;
                });
                await prepared.Invoke(context);
            };
        });

        return app;
    }

    private static PreparedProxyPipeline Prepare(TrustedProxySnapshot snapshot, RequestDelegate next, ILoggerFactory logger)
    {
        if (snapshot.Proxies.IsEmpty)
        { return new(snapshot, next); }
        var options = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor, ForwardLimit = 1 };
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
        foreach (var network in snapshot.Proxies.Networks)
        { options.KnownIPNetworks.Add(network); }
        var middleware = new ForwardedHeadersMiddleware(next, logger, Options.Create(options));
        return new(snapshot, middleware.Invoke);
    }
    private sealed record PreparedProxyPipeline(TrustedProxySnapshot Snapshot, RequestDelegate Invoke);

    private static bool IsHttps(HttpContext context, TrustedProxies proxies)
    {
        if (!proxies.Contains(context.Connection.RemoteIpAddress))
        {
            return context.Request.IsHttps;
        }

        var forwarded = context.Request.Headers[ForwardedHeadersDefaults.XForwardedProtoHeaderName].ToString();
        var last = forwarded.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
        return string.Equals(last, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "已忽略 ASPNETCORE_FORWARDEDHEADERS_ENABLED：转发头只按部署参数 TenonEdge:TrustedProxies 与系统设置登记的代理处理。")]
    private static partial void LogForwardedHeadersVariableIgnored(ILogger logger);

    private sealed record RequestProtocolFeature(bool IsHttps) : IRequestProtocolFeature;
}
