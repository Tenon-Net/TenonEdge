using Microsoft.AspNetCore.Http.HttpResults;
using TenonEdge.Adapters.Diagnostics;
using TenonEdge.Host.Bootstrap;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Security;
using TenonEdge.Runtime.Diagnostics;

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
        group.MapPost("/review", Review).RequireAuthorization(AuthorizationPolicies.Configurator).WithName("reviewModuleCredential")
            .WithSummary("核对扩展凭证的非秘密接收方、账户及动态积存，不接收秘密、不保存或联网")
            .WithDescription("核对不冻结队列或授权保存，确认仍由原写入事务重新检查版本及账户声明。")
            .WithMetadata(new RequestBodyLimit(16 * 1024)).ProducesProblem(403).ProducesProblem(409).ProducesProblem(429);
        return api;
    }
    private static Results<Ok<ModuleCredentialReview>, ValidationProblem, ProblemHttpResult> Review(long connectionId,
        ModuleCredentialReviewRequest request, ModuleCredentialStore store, PreviewOperationBudget budget, HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        using var admission = ConnectionTestAdmission.TryAcquire(context.User, budget, out var failure);
        if (failure is not null)
        { return failure; }
        try
        { return TypedResults.Ok(store.Review(connectionId, request, context.User.IsInRole(nameof(UserRole.Administrator)))); }
        catch (ArgumentException) { return ApiProblems.Validation(new Dictionary<string, string[]> { ["credential"] = ["请核对账户、绑定声明和保留操作；核对入口不接收秘密。"] }); }
        catch (CredentialWriteException error) { return Failure(error); }
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
