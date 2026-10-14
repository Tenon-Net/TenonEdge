using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Data.Sqlite;
using TenonEdge.Host.Bootstrap;
using TenonEdge.Host.Security;

namespace TenonEdge.Host.Features.Tokens;

internal static class ApiTokenEndpoints
{
    public static RouteGroupBuilder MapApiTokenEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/tokens").WithTags("Tokens").RequireAuthorization(AuthorizationPolicies.Administrator)
            .ProducesProblem(400).ProducesProblem(403).ProducesProblem(409).ProducesProblem(503);
        group.MapGet("", List).WithName("listApiTokens").WithSummary("管理员分页查看令牌元数据，永不返回原文或哈希");
        group.MapPost("", Create).WithName("createApiToken").WithSummary("仅HTTPS创建固定角色令牌，原文只返回一次")
            .WithDescription("名称1—64字符且不可复用；角色和有效期不可修改，最多1000个尚未吊销令牌（含过期项）。HTTPS Cookie仍须CSRF。响应丢失不得自动重放：核对列表，吊销后换名创建。管理员令牌创建的令牌沿用原来源账号归属，创建动作另记操作者令牌；不继承创建者后续角色/启停状态。");
        group.MapPost("/{id:long}/revoke", Revoke).WithName("revokeApiToken").WithSummary("明确确认后吊销令牌，下一请求立即拒绝")
            .WithDescription("不撤销已接管的后台操作，不删除历史元数据或审计；不可取消吊销，重复确认409而不再次记成功。");
        api.MapGet("/users/{userId:long}/tokens", Creator).WithTags("Users").RequireAuthorization(AuthorizationPolicies.Administrator)
            .WithName("getUserCreatedTokens").WithSummary("停用/删除前核对该来源账号尚未吊销的令牌，显式选择保留或吊销")
            .WithDescription("包括管理员令牌代建且沿用该来源账号的令牌；不是动态权限继承。Version仅绑定不可变清单，不受lastUsed变化影响。过期项仍显示；与用户修改事务内重核，清单变化须重新确认。")
            .ProducesProblem(403).ProducesProblem(404).ProducesProblem(503);
        return api;
    }
    private static Results<Ok<ApiTokenPage>, ProblemHttpResult> List(long? after, int? limit, ApiTokenStore store, HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        try
        { return TypedResults.Ok(store.Read(after ?? 0, limit ?? 50)); }
        catch (ApiTokenException error) { return Problem(error); }
        catch (SqliteException) { return Storage(); }
    }
    private static Results<Ok<ApiTokenCreated>, ProblemHttpResult> Create(ApiTokenCreateRequest request, ApiTokenStore store, HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!RequestProtocol.IsHttps(context))
        { return Problem(new(403, "token.httpsRequired")); }
        try
        { return TypedResults.Ok(store.Create(request, ManagementActor.Capture(context))); }
        catch (ApiTokenException error) { return Problem(error); }
        catch (SqliteException) { return Storage(); }
    }
    private static Results<NoContent, ProblemHttpResult> Revoke(long id, ApiTokenRevokeRequest request, ApiTokenStore store, HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        try
        { store.Revoke(id, request.Confirmed, ManagementActor.Capture(context)); return TypedResults.NoContent(); }
        catch (ApiTokenException error) { return Problem(error); }
        catch (SqliteException) { return Storage(); }
    }
    private static Results<Ok<CreatorTokenReview>, ProblemHttpResult> Creator(long userId, ApiTokenStore store, HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        try
        { return TypedResults.Ok(store.ReadCreator(userId)); }
        catch (ApiTokenException error) { return Problem(error); }
        catch (SqliteException) { return Storage(); }
    }
    internal static ProblemHttpResult Problem(ApiTokenException error) => ApiProblems.Problem(error.Status, error.Code,
        "令牌操作未执行。请核对HTTPS、权限、最新清单、确认及输入；不可自动重放。");
    private static ProblemHttpResult Storage() => Problem(new(503, "token.storageUnavailable"));
}
