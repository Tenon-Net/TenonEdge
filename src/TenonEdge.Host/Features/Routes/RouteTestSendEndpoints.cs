using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.HttpResults;
using TenonEdge.Adapters.Http;
using TenonEdge.Host.Bootstrap;
using TenonEdge.Host.Features.Configuration;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Tokens;
using TenonEdge.Host.Security;
using TenonEdge.Runtime.Diagnostics;

namespace TenonEdge.Host.Features.Routes;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record RouteTestSendPrepareRequest(Guid Id);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record RouteTestSendConfirmRequest(Guid Id, Guid ConfirmationId, bool Confirmed);

internal static class RouteTestSendEndpoints
{
    internal static void Map(RouteGroupBuilder api)
    {
        api.MapPost("/routes/test-send/prepare", Prepare).WithTags("Routes").WithName("prepareRouteTestSend")
            .WithSummary("核对本会话原预览的通用输出目标、完整字节数与指纹；不发送")
            .WithDescription("只接受服务端原预览编号，不接受客户端正文/地址。准备不延长五分钟产物期限；再次准备使旧确认失效。")
            .RequireAuthorization(AuthorizationPolicies.Configurator).WithMetadata(new RequestBodyLimit(1024))
            .ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(429);
        api.MapPost("/routes/test-send/confirm", Confirm).WithTags("Routes").WithName("confirmRouteTestSend")
            .WithSummary("确认后向原通用输出真实发送一次完整预览报文；不入队、不重试")
            .WithDescription("一次消费确认。包括展示被截断时的完整原报文，编号和时间不重新生成。结果未知/丢回复也不能重放；必须重新预览并确认。全局/会话共享预览预算，最多30秒。TenonIoT不提供。")
            .RequireAuthorization(AuthorizationPolicies.Configurator).WithMetadata(new RequestBodyLimit(1024))
            .ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(429);
    }

    private static Results<Ok<RouteTestSendReview>, ProblemHttpResult, ValidationProblem> Prepare(RouteTestSendPrepareRequest request,
        RouteTestSendService service, PreviewOperationBudget budget, HttpContext context)
    {
        using var lease = ConnectionTestAdmission.TryAcquire(context.User, budget, out var failure);
        if (failure is not null)
        { return failure; }
        context.Response.Headers.CacheControl = "no-store";
        try
        { return TypedResults.Ok(service.Prepare(request.Id, ManagementIdentity.OwnerKey(context.User)!, context.RequestAborted)); }
        catch (RouteTestSendException error) { return Problem(error.Status, error.Code); }
        catch (DraftWriteException error) { return Problem(409, error.Code); }
        catch (ArgumentException) { return Invalid(); }
    }

    private static async Task<Results<Ok<RouteTestSendResult>, ProblemHttpResult, ValidationProblem>> Confirm(RouteTestSendConfirmRequest request,
        RouteTestSendService service, PreviewOperationBudget budget, HttpContext context)
    {
        using var lease = ConnectionTestAdmission.TryAcquire(context.User, budget, out var failure);
        if (failure is not null)
        { return failure; }
        context.Response.Headers.CacheControl = "no-store";
        try
        {
            return TypedResults.Ok(await service.ConfirmAsync(request.Id, request.ConfirmationId, request.Confirmed,
                ManagementIdentity.OwnerKey(context.User)!, ManagementActor.Capture(context), context.RequestAborted));
        }
        catch (RouteTestSendException error) { return Problem(error.Status, error.Code); }
        catch (DraftWriteException error) { return Problem(409, error.Code); }
        catch (ApiTokenException error) { return Problem(error.Status, error.Code); }
        catch (HttpConnectionUnavailableException) { return Problem(409, "connection.inUse"); }
        catch (Exception error) when (error is ArgumentException or FormatException) { return Invalid(); }
    }

    private static ProblemHttpResult Problem(int status, string code) => ApiProblems.Problem(status, code,
        "测试产物、确认、草稿或凭证已不可用，请重新核对。确认已接管后不会恢复或重试；请勿自动重放请求。");
    private static ValidationProblem Invalid() => ApiProblems.Validation(new Dictionary<string, string[]>
    { ["testSend"] = ["须使用本会话的原预览和最新确认编号，并明确确认真实发送风险；不能提交正文或目标参数。"] });
}
