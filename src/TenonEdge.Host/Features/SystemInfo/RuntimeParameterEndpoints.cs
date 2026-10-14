using Microsoft.AspNetCore.Http.HttpResults;
using TenonEdge.Host.Bootstrap;
using TenonEdge.Host.Security;

namespace TenonEdge.Host.Features.SystemInfo;

internal static class RuntimeParameterEndpoints
{
    public static RouteGroupBuilder MapRuntimeParameterEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/system/runtime-parameters").WithTags("System");
        group.MapGet("/", Read).WithName("getRuntimeParameters").WithSummary("读取当前有效的审计保留期与共享预览并发参数");
        group.MapPut("/", Write).RequireAuthorization(AuthorizationPolicies.Administrator)
            .WithName("writeRuntimeParameters").WithSummary("管理员确认后持久化运行参数并立即供后续准入及清理采用")
            .WithDescription("版本条件、确认和审计同事务。降低并发不取消已有租约；审计不得短于180天或部署下限。清理/导出正在执行时立即拒绝，不排队。无草稿/运行版本变化。")
            .WithMetadata(new RequestBodyLimit(4096)).ProducesProblem(409).ProducesProblem(413);
        return api;
    }
    private static Ok<RuntimeParameterState> Read(RuntimeParameterStore store, HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        return TypedResults.Ok(store.Read());
    }
    private static Results<Ok<RuntimeParameterState>, ValidationProblem, ProblemHttpResult> Write(
        RuntimeParameterRequest request, RuntimeParameterStore store, HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        try
        { return TypedResults.Ok(store.Write(request, ManagementIdentity.Audit(context.User), SourceAddress.Of(context))); }
        catch (ArgumentException exception)
        { return ApiProblems.Validation(new Dictionary<string, string[]> { ["parameters"] = [exception.Message] }); }
        catch (RuntimeParameterConflictException)
        { return ApiProblems.Problem(409, "system.parametersConflict", "运行参数已变化，请重新读取后确认。"); }
        catch (RuntimeParameterBusyException)
        { return ApiProblems.Problem(409, "system.parametersBusy", "审计清理或导出正在执行，请结束后重新读取并确认。"); }
    }
}
