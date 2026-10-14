using Microsoft.AspNetCore.Http.HttpResults;
using TenonEdge.Host.Bootstrap;
using TenonEdge.Host.Security;

namespace TenonEdge.Host.Features.SystemInfo;

internal static class QueueParameterEndpoints
{
    public static RouteGroupBuilder MapQueueParameterEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/system/queue-parameters").WithTags("System");
        group.MapGet("/", Read).WithName("getQueueParameters").WithSummary("读取当前队列配额、保留期和磁盘保护参数");
        group.MapPut("/", Write).RequireAuthorization(AuthorizationPolicies.Administrator)
            .WithName("writeQueueParameters").WithSummary("管理员确认后持久化队列运行参数，后续队列事务采用新值")
            .WithDescription("与审计同事务；版本条件、明确确认。降低配额不删除旧记录；缩短保留期影响下一轮清理。原队列事务正在使用参数时409，不排队。仅通用输出，平台去重窗口在T4联动。")
            .WithMetadata(new RequestBodyLimit(4096)).ProducesProblem(409).ProducesProblem(413);
        return api;
    }
    private static Ok<QueueParameterState> Read(QueueParameterStore store, HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        return TypedResults.Ok(store.Read());
    }
    private static Results<Ok<QueueParameterState>, ValidationProblem, ProblemHttpResult> Write(
        QueueParameterRequest request, QueueParameterStore store, HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        try
        { return TypedResults.Ok(store.Write(request, ManagementIdentity.Audit(context.User), SourceAddress.Of(context))); }
        catch (ArgumentException exception)
        { return ApiProblems.Validation(new Dictionary<string, string[]> { ["parameters"] = [exception.Message] }); }
        catch (RuntimeParameterConflictException)
        { return ApiProblems.Problem(409, "system.queueParametersConflict", "队列参数已变化，请重新读取后确认。"); }
        catch (QueueParameterBusyException)
        { return ApiProblems.Problem(409, "system.queueParametersBusy", "队列写入或过期清理正在使用参数，请稍后重新读取并确认。"); }
    }
}
