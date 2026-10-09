using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using TenonEdge.Host.Bootstrap;
using TenonEdge.Host.Features.Configuration;
using TenonEdge.Host.Security;

namespace TenonEdge.Host.Features.Routes;

internal static class RouteDraftEndpoints
{
    public static RouteGroupBuilder MapRouteDraftEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/routes").WithTags("Routes").WithMetadata(new RequestBodyLimit(1024 * 1024));
        group.MapGet("", (RouteDraftStore store) => TypedResults.Ok(store.Read()))
            .WithName("getRouteDrafts").WithSummary("查看路线草稿摘要，尚未编译，不代表运行状态");
        group.MapGet("/{routeId:long}", Read).WithName("getRouteDraft").WithSummary("读取自有图草稿及全局版本").ProducesProblem(404);
        group.MapPost("", (RouteDraftRequest request, RouteDraftStore store, HttpContext context) => Write(null, request, store, context))
            .RequireAuthorization(AuthorizationPolicies.Configurator).WithName("createRouteDraft").WithSummary("新建路线草稿，保存不应用")
            .ProducesProblem(409).ProducesProblem(403).ProducesProblem(413);
        group.MapPut("/{routeId:long}", (long routeId, RouteDraftRequest request, RouteDraftStore store, HttpContext context) => Write(routeId, request, store, context))
            .RequireAuthorization(AuthorizationPolicies.Configurator).WithName("updateRouteDraft").WithSummary("保存路线草稿，key 不可修改")
            .ProducesProblem(409).ProducesProblem(404).ProducesProblem(403).ProducesProblem(413);
        group.MapPost("/{routeId:long}/copy", Copy).RequireAuthorization(AuthorizationPolicies.Configurator)
            .WithName("copyRouteDraft").WithSummary("复制路线图及引用，副本默认停用").ProducesProblem(409).ProducesProblem(404).ProducesProblem(403).ProducesProblem(413);
        group.MapDelete("/{routeId:long}", Delete).RequireAuthorization(AuthorizationPolicies.Configurator)
            .WithName("deleteRouteDraft").WithSummary("删除草稿，不改变运行配置或撤回投递").ProducesProblem(409).ProducesProblem(404).ProducesProblem(403).ProducesProblem(413);
        return api;
    }

    private static Results<Ok<RouteDraftResult>, ProblemHttpResult> Read(long routeId, RouteDraftStore store)
    {
        try
        { return TypedResults.Ok(store.Read(routeId)); }
        catch (DraftWriteException error) { return Failure(error); }
    }
    private static Results<Ok<RouteDraftResult>, ValidationProblem, ProblemHttpResult> Write(long? id, RouteDraftRequest request, RouteDraftStore store, HttpContext context)
    {
        try
        { return TypedResults.Ok(store.Write(id, request, context.User.FindFirstValue(SessionClaims.AccountName), SourceAddress.Of(context))); }
        catch (Exception error) when (error is ArgumentException or JsonException) { return Invalid(); }
        catch (DraftWriteException error) { return Failure(error); }
    }
    private static Results<Ok<RouteDraftResult>, ValidationProblem, ProblemHttpResult> Copy(long routeId, RouteDraftCopyRequest request, RouteDraftStore store, HttpContext context)
    {
        try
        { return TypedResults.Ok(store.Copy(routeId, request, context.User.FindFirstValue(SessionClaims.AccountName), SourceAddress.Of(context))); }
        catch (ArgumentException) { return Invalid(); }
        catch (DraftWriteException error) { return Failure(error); }
    }
    private static Results<Ok<DraftVersion>, ProblemHttpResult> Delete(long routeId, string expectedVersion, RouteDraftStore store, HttpContext context)
    {
        try
        { return TypedResults.Ok(store.Delete(routeId, expectedVersion, context.User.FindFirstValue(SessionClaims.AccountName), SourceAddress.Of(context))); }
        catch (DraftWriteException error) { return Failure(error); }
    }
    private static ValidationProblem Invalid() => ApiProblems.Validation(new Dictionary<string, string[]> { ["route"] = ["路线参数或图格式无效；检查版本、标识、资源限制与非秘密配置。"] });
    private static ProblemHttpResult Failure(DraftWriteException error) => ApiProblems.Problem(error.Code == "route.notFound" ? 404 : 409,
        error.Code, "草稿未修改，请核对版本、唯一标识和引用关系后重试。");
}
