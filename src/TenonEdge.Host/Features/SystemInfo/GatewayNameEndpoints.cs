using Microsoft.AspNetCore.Http.HttpResults;
using TenonEdge.Host.Bootstrap;
using TenonEdge.Host.Security;

namespace TenonEdge.Host.Features.SystemInfo;

internal static class GatewayNameEndpoints
{
    public static RouteGroupBuilder MapGatewayNameEndpoints(this RouteGroupBuilder api)
    {
        api.MapPut("/system/name", Write).WithTags("System")
            .RequireAuthorization(AuthorizationPolicies.Administrator)
            .WithName("writeGatewayName").WithSummary("管理员修改网关显示名称并记录审计")
            .WithDescription("expectedName必须等于读取的gatewayName（含null）；只比较此名称字段，不代表其他设置版本。名称去首尾空白，空名称清为未命名；不改变NodeId、草稿或运行版本。相同规范化名称不重复写审计。")
            .ProducesProblem(409);
        return api;
    }

    private static Results<Ok<GatewayNameResult>, ValidationProblem, ProblemHttpResult> Write(
        GatewayNameRequest request, GatewayNameStore store, HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        try
        { return TypedResults.Ok(store.Write(request, ManagementIdentity.Audit(context.User), SourceAddress.Of(context))); }
        catch (ArgumentException exception)
        { return ApiProblems.Validation(new Dictionary<string, string[]> { ["name"] = [exception.Message] }); }
        catch (GatewayNameConflictException)
        { return ApiProblems.Problem(409, "system.gatewayNameConflict", "网关名称已变化，请重新读取后操作。"); }
    }
}
