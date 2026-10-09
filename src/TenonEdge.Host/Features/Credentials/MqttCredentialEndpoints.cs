using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using TenonEdge.Host.Bootstrap;
using TenonEdge.Host.Security;

namespace TenonEdge.Host.Features.Credentials;

internal static class MqttCredentialEndpoints
{
    public static RouteGroupBuilder MapMqttCredentialEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/connections/{connectionId:long}").WithTags("Credentials");
        group.MapGet("/mqtt-credential", Get).WithName("getMqttCredentialStatus").WithSummary("查看 MQTT 凭证状态（不返回秘密）");
        group.MapPut("/mqtt-credential", Put).RequireAuthorization(AuthorizationPolicies.Configurator)
            .WithName("writeMqttCredential").WithSummary("写入 MQTT 凭证或更新接收方绑定")
            .WithDescription("草稿连接须带 expectedDraftVersion，协议及接收方必须匹配该版本；已有凭证另带 expectedVersion。凭证立即生效，不推进草稿版本。种子编号预置入口仍保留；retainExisting 仅管理员可用。")
            .ProducesProblem(409).ProducesProblem(403);
        group.MapDelete("/mqtt-credential", Clear).RequireAuthorization(AuthorizationPolicies.Configurator)
            .WithName("clearMqttCredential").WithSummary("清除未被保留运行版本引用的草稿连接认证凭证")
            .ProducesProblem(403).ProducesProblem(409);
        return api;
    }

    private static Results<NoContent, ProblemHttpResult> Clear(long connectionId, string expectedDraftVersion,
        string expectedVersion, MqttCredentialStore store, HttpContext context)
    {
        try
        {
            store.Clear(connectionId, expectedDraftVersion, expectedVersion, context.User.FindFirstValue(SessionClaims.AccountName), SourceAddress.Of(context));
            return TypedResults.NoContent();
        }
        catch (CredentialWriteException exception)
        { return ApiProblems.Problem(409, exception.Code, "凭证未清除，请核对草稿、凭证版本及运行版本引用。"); }
    }

    private static Results<Ok<MqttCredentialMetadata>, NotFound> Get(long connectionId, MqttCredentialStore store) =>
        store.Read(connectionId) is { } value ? TypedResults.Ok(value) : TypedResults.NotFound();

    private static Results<Ok<MqttCredentialMetadata>, ValidationProblem, ProblemHttpResult> Put(
        long connectionId, MqttCredentialRequest request, MqttCredentialStore store, HttpContext context)
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
