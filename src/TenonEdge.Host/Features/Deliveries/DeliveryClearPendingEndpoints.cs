using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using TenonEdge.Host.Bootstrap;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Security;
using TenonEdge.Runtime.Deliveries;

namespace TenonEdge.Host.Features.Deliveries;

internal static class DeliveryClearPendingEndpoints
{
    public static RouteGroupBuilder MapDeliveryClearPendingEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/connections/{connectionId:long}/clear-pending", Clear).WithTags("Deliveries")
            .RequireAuthorization(AuthorizationPolicies.Administrator)
            .WithName("clearPendingDeliveries").WithSummary("管理员确认后清空目标全部待发记录")
            .WithDescription("绑定有效版本；等待原发送者提交在途结果后，在唯一写者事务中删除当时仍待发的全部记录并累计人工丢弃。不是核对时的固定清单：期间新入队记录可能包括在内，删除边界之后入队的保留。不清理失败/隔离、不改变暂停或退避；超时不删除。不自动重试，响应丢失只重新读取。请求断开不撤销已接受操作。")
            .ProducesProblem(400).ProducesProblem(403).ProducesProblem(409).ProducesProblem(503);
        return api;
    }

    private static async Task<Results<Ok<DeliveryCleanupResponse>, ProblemHttpResult>> Clear(long connectionId,
        DeliveryClearPendingRequest request, RuntimeCoordinator coordinator, HttpContext context, CancellationToken token)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!request.Confirmed || request.Revision < 1 || connectionId is < 1 or > 9007199254740991L)
        { return ApiProblems.Problem(400, "delivery.invalidClearPending", "须明确确认目标及当前有效版本，清空执行时仍待发的全部记录。"); }
        try
        {
            var result = await coordinator.ClearPendingDeliveriesAsync(connectionId, request.Revision,
                context.User.FindFirstValue(SessionClaims.AccountName)!, SourceAddress.Of(context), token);
            if (result.Committed)
            { return TypedResults.Ok(new DeliveryCleanupResponse(result.AffectedRecords)); }
            return ApiProblems.Problem(result.Status == DeliveryWriteStatus.Rejected ? 409 : 503,
                result.Status == DeliveryWriteStatus.Rejected ? result.Error! : "delivery.clearPendingOutcomeUnknown",
                "未完成清空或结果不可确定。原发送者未及时到达安全边界时不删除；请重新读取，不自动重放。");
        }
        catch (DeliveryClearPendingUnavailableException)
        { return ApiProblems.Problem(409, "delivery.clearPendingUnavailable", "运行版本或队列操作状态已变化，请重新读取目标后确认。"); }
    }
}

internal sealed record DeliveryClearPendingRequest(long Revision, bool Confirmed);
