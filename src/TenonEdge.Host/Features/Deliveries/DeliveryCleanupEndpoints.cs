using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using TenonEdge.Host.Bootstrap;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Security;
using TenonEdge.Runtime.Deliveries;

namespace TenonEdge.Host.Features.Deliveries;

internal static class DeliveryCleanupEndpoints
{
    public static RouteGroupBuilder MapDeliveryCleanupEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/deliveries/cleanup", Cleanup).WithTags("Deliveries")
            .RequireAuthorization(AuthorizationPolicies.Administrator)
            .WithName("cleanupDeliveryRecords").WithSummary("管理员确认后永久清理选定的失败或隔离记录")
            .WithDescription("最多1000条，绑定区域、稳定编号、seq、目标及迁移代次；全部匹配才原子删除，不按执行时筛选扩大选择，不清空待发、不发送。记录人工清理计数及审计。响应丢失只重新查询，不自动重放；请求取消不撤销已接收操作。")
            .ProducesProblem(400).ProducesProblem(403).ProducesProblem(409).ProducesProblem(503);
        return api;
    }

    private static async Task<Results<Ok<DeliveryCleanupResponse>, ProblemHttpResult>> Cleanup(DeliveryCleanupRequest request,
        RuntimeCoordinator coordinator, HttpContext context, CancellationToken token)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!request.Confirmed || request.Items is null)
        { return Invalid(); }
        try
        {
            var selection = new DeliveryCleanupSelection(request.State, request.Items);
            var result = await coordinator.CleanupDeliveriesAsync(selection,
                context.User.FindFirstValue(SessionClaims.AccountName)!, SourceAddress.Of(context), token);
            if (result.Committed)
            { return TypedResults.Ok(new DeliveryCleanupResponse(result.AffectedRecords)); }
            return ApiProblems.Problem(result.Status == DeliveryWriteStatus.Rejected ? 409 : 503,
                result.Status == DeliveryWriteStatus.Rejected ? result.Error! : "delivery.cleanupOutcomeUnknown",
                "未能完成清理或结果不可确定。请重新查询并选择，不自动重放；记录状态、目标或迁移代次变化会整体拒绝。");
        }
        catch (ArgumentException) { return Invalid(); }
        catch (DeliveryCleanupUnavailableException)
        { return ApiProblems.Problem(409, "delivery.cleanupUnavailable", "队列写者不可用或有其他运行操作，请重新读取后再选择。"); }
    }

    private static ProblemHttpResult Invalid() => ApiProblems.Problem(400, "delivery.invalidCleanupSelection",
        "须明确确认并选择1至1000条不同的失败或隔离记录，提供其稳定编号、seq、目标及迁移代次。");
}

internal sealed record DeliveryCleanupRequest(DeliveryState State, bool Confirmed, DeliveryCleanupItem[]? Items);
internal sealed record DeliveryCleanupResponse(int ClearedRecords);
