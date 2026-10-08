using System.Net.Mime;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace TenonEdge.Host.Features.Health;

/// <summary>
/// 匿名健康检查端点（总设计 §10.1）：<c>/healthz</c> 表示进程存活，<c>/readyz</c> 表示宿主与运行时已就绪（处于“运行失败”时为 503）。
/// 两者只返回状态文本，不返回配置、版本或错误细节。
/// </summary>
internal static class HealthEndpoints
{
    public const string LivenessPath = "/healthz";

    public const string ReadinessPath = "/readyz";

    /// <summary>就绪检查的标签。其他组件以同一标签注册自己的就绪条件。</summary>
    public const string ReadinessTag = "ready";

    public static IServiceCollection AddHealthEndpointChecks(this IServiceCollection services)
    {
        services.AddHealthChecks()
            .AddCheck<HostLifecycleReadinessCheck>("host-lifecycle", tags: [ReadinessTag])
            .AddCheck<RuntimeStartConditionsReadinessCheck>("runtime-start-conditions", tags: [ReadinessTag])
            .AddCheck<RuntimeReadinessCheck>("runtime", tags: [ReadinessTag]);

        return services;
    }

    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(LivenessPath, GetLiveness)
            .AllowAnonymous()
            .WithTags("Health")
            .WithName("getLiveness")
            .WithSummary("进程存活检查")
            .WithDescription("进程能够处理请求即返回 200 与文本 Healthy。")
            .Produces<string>(StatusCodes.Status200OK, MediaTypeNames.Text.Plain);

        endpoints.MapGet(ReadinessPath, GetReadinessAsync)
            .AllowAnonymous()
            .WithTags("Health")
            .WithName("getReadiness")
            .WithSummary("就绪检查")
            .WithDescription("全部就绪条件满足时返回 200；任一条件不满足时返回 503。只返回状态文本，不返回原因。")
            .Produces<string>(StatusCodes.Status200OK, MediaTypeNames.Text.Plain)
            .Produces<string>(StatusCodes.Status503ServiceUnavailable, MediaTypeNames.Text.Plain);

        return endpoints;
    }

    private static ContentHttpResult GetLiveness(HttpResponse response)
    {
        DisableCaching(response);
        return TypedResults.Text(nameof(HealthStatus.Healthy), MediaTypeNames.Text.Plain);
    }

    private static async Task<ContentHttpResult> GetReadinessAsync(
        HealthCheckService healthChecks,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        var report = await healthChecks.CheckHealthAsync(
            registration => registration.Tags.Contains(ReadinessTag),
            cancellationToken);

        DisableCaching(response);
        var statusCode = report.Status == HealthStatus.Unhealthy
            ? StatusCodes.Status503ServiceUnavailable
            : StatusCodes.Status200OK;

        return TypedResults.Text(report.Status.ToString(), MediaTypeNames.Text.Plain, statusCode: statusCode);
    }

    private static void DisableCaching(HttpResponse response) => response.Headers.CacheControl = "no-store";
}
