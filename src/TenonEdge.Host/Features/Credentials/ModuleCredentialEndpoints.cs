using Microsoft.AspNetCore.Http.HttpResults;
using TenonEdge.Host.Bootstrap;
using TenonEdge.Host.Security;

namespace TenonEdge.Host.Features.Credentials;

internal static class ModuleCredentialEndpoints
{
    public static RouteGroupBuilder MapModuleCredentialEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/connections/{connectionId:long}/module-credential").WithTags("Credentials");
        group.MapGet("/", Get).WithName("getModuleCredentialStatus").WithSummary("查看扩展连接凭证状态，不返回秘密");
        group.MapPut("/", Put).RequireAuthorization(AuthorizationPolicies.Configurator).WithName("writeModuleCredential")
            .WithSummary("独立写入扩展连接凭证，补齐导入待补标记时更新草稿版本").WithMetadata(new RequestBodyLimit(256 * 1024))
            .ProducesProblem(403).ProducesProblem(409).ProducesProblem(413);
        group.MapDelete("/", Clear).RequireAuthorization(AuthorizationPolicies.Configurator).WithName("clearModuleCredential")
            .WithSummary("清除未被运行版本引用且无投递的扩展连接凭证").ProducesProblem(403).ProducesProblem(409);
        return api;
    }
    private static Results<Ok<ModuleCredentialMetadata>, NotFound> Get(long connectionId, ModuleCredentialStore store, HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        return store.Read(connectionId) is { } metadata ? TypedResults.Ok(metadata) : TypedResults.NotFound();
    }
    private static async Task<Results<Ok<ModuleCredentialMetadata>, ValidationProblem, ProblemHttpResult>> Put(long connectionId,
        ModuleCredentialRequest request, ModuleCredentialStore store, HttpContext context)
    {
        try
        {
            return TypedResults.Ok(await store.WriteAsync(connectionId, request, ManagementIdentity.Audit(context.User),
                SourceAddress.Of(context), context.User.IsInRole(nameof(UserRole.Administrator)), context.RequestAborted));
        }
        catch (ArgumentException) { return ApiProblems.Validation(new Dictionary<string, string[]> { ["credential"] = ["凭证字段、账户、绑定声明或材料无效。"] }); }
        catch (CredentialWriteException error) { return Failure(error); }
    }
    private static Results<NoContent, ProblemHttpResult> Clear(long connectionId, string expectedDraftVersion, string expectedVersion,
        ModuleCredentialStore store, HttpContext context)
    {
        try
        {
            store.Clear(connectionId, expectedDraftVersion, expectedVersion, ManagementIdentity.Audit(context.User), SourceAddress.Of(context));
            return TypedResults.NoContent();
        }
        catch (CredentialWriteException error) { return Failure(error); }
    }
    private static ProblemHttpResult Failure(CredentialWriteException error) => ApiProblems.Problem(
        error.Code == "credential.administratorRequired" ? 403 : 409, error.Code,
        "凭证未修改，请重新核对权限、草稿/凭证版本、账户声明和引用关系。");
}
