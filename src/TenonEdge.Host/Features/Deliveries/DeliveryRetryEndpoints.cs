using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using TenonEdge.Host.Bootstrap;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Security;
using TenonEdge.Runtime.Deliveries;

namespace TenonEdge.Host.Features.Deliveries;

internal static class DeliveryRetryEndpoints
{
    public static RouteGroupBuilder MapDeliveryRetryEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/deliveries/retry", Retry).WithTags("Deliveries")
            .RequireAuthorization(AuthorizationPolicies.Configurator)
            .WithName("retryFailedDeliveries").WithSummary("将选定失败记录原样移回队尾，可能产生重复")
            .WithDescription("绑定有效运行版本和每条稳定编号/seq代次，最多1000条；全部状态/身份/配额检查通过才原子重排。只重试失败区，不解除暂停、不改变报文或目标。200只代表队列已提交，不代表发送成功。请求超时/响应丢失只重新查询，不自动重放；旧代次不能重复执行。")
            .ProducesProblem(400).ProducesProblem(403).ProducesProblem(409).ProducesProblem(503);
        return api;
    }

    private static async Task<Results<Ok<DeliveryRetryResponse>, ProblemHttpResult>> Retry(DeliveryRetryRequest request,
        RuntimeCoordinator coordinator, HttpContext context, CancellationToken token)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (request.Items is null)
        { return Invalid(); }
        try
        {
            var result = await coordinator.RetryFailedDeliveriesAsync(request.Revision, request.Items,
                ManagementIdentity.Audit(context.User), SourceAddress.Of(context), token);
            if (result.Committed)
            { return TypedResults.Ok(new DeliveryRetryResponse(result.AffectedRecords)); }
            var status = result.Status == DeliveryWriteStatus.Rejected ? 409 : 503;
            return ApiProblems.Problem(status, result.Status == DeliveryWriteStatus.Rejected ? result.Error! : "delivery.retryOutcomeUnknown",
                "失败记录未能完成重试或结果不可确定。请重新读取列表，核对状态、目标和配额；不要自动重放。");
        }
        catch (ArgumentException) { return Invalid(); }
        catch (DeliveryRetryUnavailableException)
        { return ApiProblems.Problem(409, "delivery.retryUnavailable", "版本已变化、没有可用输出或有其他运行操作，请重新读取后再选择。"); }
    }

    private static ProblemHttpResult Invalid() => ApiProblems.Problem(400, "delivery.invalidRetrySelection", "请选择1至1000条不同的失败记录，并提供有效运行版本、稳定编号及seq代次。");
}

internal sealed record DeliveryRetryRequest(long Revision, DeliveryRetryItem[]? Items);
internal sealed record DeliveryRetryResponse(int RequeuedRecords);
