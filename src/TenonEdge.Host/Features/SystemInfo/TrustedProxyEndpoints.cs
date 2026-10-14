using Microsoft.AspNetCore.Http.HttpResults;
using TenonEdge.Host.Bootstrap;
using TenonEdge.Host.Features.Tokens;
using TenonEdge.Host.Security;

namespace TenonEdge.Host.Features.SystemInfo;

internal static class TrustedProxyEndpoints
{
    public static RouteGroupBuilder MapTrustedProxyEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/system/trusted-proxies").WithTags("System");
        group.MapGet("/", Read).WithName("getTrustedProxies").WithSummary("查看网页代理信任与只读部署信任；规则取并集");
        group.MapPut("/", Write).RequireAuthorization(AuthorizationPolicies.Administrator)
            .WithName("writeTrustedProxies").WithSummary("管理员核对风险后版本化保存代理信任，后续请求采用新值")
            .WithDescription("只信任明确的IP/CIDR，只处理一层来源与最后一个协议值；不能撤销部署信任。新增信任可改变Cookie协议、Token准入、限流与审计来源，不应信任整个客户端网段。")
            .WithMetadata(new RequestBodyLimit(128 * 1024)).ProducesProblem(409).ProducesProblem(413);
        return api;
    }
    private static Ok<TrustedProxyState> Read(TrustedProxyStore store, HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        return TypedResults.Ok(store.Read());
    }
    private static Results<Ok<TrustedProxyState>, ValidationProblem, ProblemHttpResult> Write(
        TrustedProxyRequest request, TrustedProxyStore store, HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        try
        { return TypedResults.Ok(store.Write(request, ManagementActor.Capture(context))); }
        catch (ArgumentException error)
        { return ApiProblems.Validation(new Dictionary<string, string[]> { ["networks"] = [error.Message] }); }
        catch (TrustedProxyConflictException)
        { return ApiProblems.Problem(409, "system.trustedProxiesConflict", "代理信任已变化，请重新读取后确认。"); }
        catch (ApiTokenException error)
        { return ApiProblems.Problem(error.Status, error.Code, "当前操作身份已变化，请重新认证并核对。"); }
    }
}
