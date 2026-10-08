using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using TenonEdge.Host.Bootstrap;
using TenonEdge.Host.Security;

namespace TenonEdge.Host.Features.Credentials;

internal static class HttpCredentialEndpoints
{
    public static RouteGroupBuilder MapHttpCredentialEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/connections/{connectionId:long}").WithTags("Credentials");
        group.MapGet("/http-credential", Get).WithName("getHttpCredentialStatus").WithSummary("查看 HTTP 凭证状态（不返回秘密）");
        group.MapPut("/http-credential", Put).RequireAuthorization(AuthorizationPolicies.Configurator)
            .WithName("writeHttpCredential").WithSummary("写入 HTTP 凭证或更新接收方绑定")
            .WithDescription("T2 种子连接专用：ID 与种子配置一致，秘密不进入种子；已有凭证须带 expectedVersion。变更接收方默认作废，retainExisting 仅管理员可用。")
            .ProducesProblem(409).ProducesProblem(403);
        return api;
    }

    private static Results<Ok<HttpCredentialMetadata>, NotFound> Get(long connectionId, HttpCredentialStore store) =>
        store.Read(connectionId) is { } value ? TypedResults.Ok(value) : TypedResults.NotFound();

    private static Results<Ok<HttpCredentialMetadata>, ValidationProblem, ProblemHttpResult> Put(
        long connectionId, HttpCredentialRequest request, HttpCredentialStore store, HttpContext context)
    {
        try
        {
            return TypedResults.Ok(store.Write(connectionId, request, context.User.FindFirstValue(SessionClaims.AccountName),
                SourceAddress.Of(context), context.User.IsInRole(nameof(UserRole.Administrator))));
        }
        catch (ArgumentException exception)
        {
            return ApiProblems.Validation(new Dictionary<string, string[]> { ["credential"] = [exception.Message] });
        }
        catch (CredentialWriteException exception)
        {
            var status = exception.Code == "credential.administratorRequired" ? StatusCodes.Status403Forbidden : StatusCodes.Status409Conflict;
            return ApiProblems.Problem(status, exception.Code, "凭证未更新，请核对权限、版本和账户变更声明。");
        }
    }
}
