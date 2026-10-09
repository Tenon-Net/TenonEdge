using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using TenonEdge.Host.Bootstrap;
using TenonEdge.Host.Features.Configuration;
using TenonEdge.Host.Security;

namespace TenonEdge.Host.Features.Connections;

internal static class ConnectionDraftEndpoints
{
    public static RouteGroupBuilder MapConnectionDraftEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/connections").WithTags("Connections");
        group.MapGet("", (ConnectionDraftStore store) => TypedResults.Ok(store.Read()))
            .WithName("getConnectionDrafts").WithSummary("查看连接草稿及全局草稿版本，不代表运行状态");
        group.MapPost("", (ConnectionDraftRequest request, ConnectionDraftStore store, HttpContext context) => Write(null, request, store, context))
            .RequireAuthorization(AuthorizationPolicies.Configurator).WithName("createConnectionDraft")
            .WithSummary("新建连接草稿，保存不改变运行配置").ProducesProblem(409).ProducesProblem(403);
        group.MapPut("/{connectionId:long}", (long connectionId, ConnectionDraftRequest request, ConnectionDraftStore store, HttpContext context) => Write(connectionId, request, store, context))
            .RequireAuthorization(AuthorizationPolicies.Configurator).WithName("updateConnectionDraft")
            .WithSummary("更新连接草稿，key 与协议类型不可修改").ProducesProblem(409).ProducesProblem(404).ProducesProblem(403);
        group.MapDelete("/{connectionId:long}", Delete).RequireAuthorization(AuthorizationPolicies.Configurator)
            .WithName("deleteConnectionDraft").WithSummary("删除未被引用且无发送记录的连接草稿")
            .ProducesProblem(409).ProducesProblem(404).ProducesProblem(403);
        return api;
    }

    private static Results<Ok<ConnectionDraftResult>, ValidationProblem, ProblemHttpResult> Write(long? id,
        ConnectionDraftRequest request, ConnectionDraftStore store, HttpContext context)
    {
        try
        { return TypedResults.Ok(store.Write(id, request, context.User.FindFirstValue(SessionClaims.AccountName), SourceAddress.Of(context))); }
        catch (ArgumentException) { return ApiProblems.Validation(new Dictionary<string, string[]> { ["connection"] = ["连接参数无效，请检查标识、名称、协议字段、地址、超时及非秘密请求头。"] }); }
        catch (FormatException) { return ApiProblems.Validation(new Dictionary<string, string[]> { ["connection"] = ["连接参数格式无效。"] }); }
        catch (DraftWriteException exception) { return Failure(exception); }
    }

    private static Results<Ok<DraftVersion>, ProblemHttpResult> Delete(long connectionId, string expectedVersion,
        ConnectionDraftStore store, HttpContext context)
    {
        try
        { return TypedResults.Ok(store.Delete(connectionId, expectedVersion, context.User.FindFirstValue(SessionClaims.AccountName), SourceAddress.Of(context))); }
        catch (DraftWriteException exception) { return Failure(exception); }
    }

    private static ProblemHttpResult Failure(DraftWriteException exception) => ApiProblems.Problem(
        exception.Code == "connection.notFound" ? 404 : 409, exception.Code, "草稿未修改，请核对版本、唯一标识和引用关系后重试。");
}
