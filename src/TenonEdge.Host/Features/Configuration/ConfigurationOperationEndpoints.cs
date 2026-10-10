using Microsoft.AspNetCore.Http.HttpResults;
using TenonEdge.Host.Bootstrap;
using TenonEdge.Host.Features.Runtime;

namespace TenonEdge.Host.Features.Configuration;

internal static class ConfigurationOperationEndpoints
{
    public static RouteGroupBuilder MapConfigurationOperationEndpoints(this RouteGroupBuilder api)
    {
        var operations = api.MapGroup("/configuration/operations").WithTags("Configuration");
        operations.MapGet("/", List).WithName("listConfigurationOperations")
            .WithSummary("查看本进程最近 20 次应用/回滚的脱敏进度；不代替持久化版本历史");
        operations.MapGet("/{operationId:guid}", Get).WithName("getConfigurationOperation")
            .WithSummary("查看指定应用/回滚的进度和结果；重启或超出保留范围后返回 404")
            .ProducesProblem(404);
        return api;
    }

    private static Ok<RuntimeApplicationStatus[]> List(RuntimeCoordinator coordinator, HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        return TypedResults.Ok(coordinator.ReadApplications());
    }

    private static Results<Ok<RuntimeApplicationStatus>, ProblemHttpResult> Get(Guid operationId, RuntimeCoordinator coordinator, HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        return coordinator.ReadApplication(operationId) is { } operation ? TypedResults.Ok(operation)
            : ApiProblems.Problem(404, "configuration.operationNotFound", "操作不存在、已超出本进程保留范围或服务已重启；请核对版本历史，不要自动重试应用。");
    }
}
