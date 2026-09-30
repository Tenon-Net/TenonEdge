using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace TenonEdge.IntegrationTests.Infrastructure;

/// <summary>
/// 列出宿主实际注册的全部路由端点及其访问级别，用于核对匿名例外清单。
/// </summary>
internal static class EndpointInventory
{
    public const string Anonymous = "anonymous";
    public const string Protected = "protected";

    /// <summary>形如 <c>GET /healthz (anonymous)</c>，按字符序排序。</summary>
    public static IReadOnlyList<string> Describe(IServiceProvider services)
    {
        var fallbackPolicy = services.GetRequiredService<IOptions<AuthorizationOptions>>().Value.FallbackPolicy;

        return services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Select(endpoint => Describe(endpoint, fallbackPolicy))
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    private static string Describe(RouteEndpoint endpoint, AuthorizationPolicy? fallbackPolicy)
    {
        var methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? ["*"];
        var route = "/" + endpoint.RoutePattern.RawText?.TrimStart('/');
        var access = IsAnonymous(endpoint, fallbackPolicy) ? Anonymous : Protected;
        return $"{string.Join(',', methods)} {route} ({access})";
    }

    // 显式允许匿名，或既没有授权元数据、也没有全局回退策略的端点，都可被匿名访问。
    private static bool IsAnonymous(Endpoint endpoint, AuthorizationPolicy? fallbackPolicy)
    {
        if (endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null)
        {
            return true;
        }

        var hasAuthorization = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Count > 0
            || endpoint.Metadata.GetMetadata<AuthorizationPolicy>() is not null;
        return !hasAuthorization && fallbackPolicy is null;
    }
}
