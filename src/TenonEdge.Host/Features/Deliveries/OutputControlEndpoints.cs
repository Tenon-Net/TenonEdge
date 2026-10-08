using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Host.Bootstrap;
using TenonEdge.Host.Security;

namespace TenonEdge.Host.Features.Deliveries;

internal static class OutputControlEndpoints
{
    public static RouteGroupBuilder MapOutputControlEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/connections/{connectionId:long}").WithTags("Deliveries");
        group.MapGet("/delivery-control", (long connectionId, OutputControlStore store) => TypedResults.Ok(store.Read(connectionId)))
            .WithName("getOutputControl").WithSummary("查看目标手动暂停状态");
        group.MapPut("/delivery-control", Put).RequireAuthorization(AuthorizationPolicies.Administrator)
            .WithName("writeOutputControl").WithSummary("确认后手动暂停或恢复目标")
            .WithDescription("暂停持久保存；已经在途的一条发送允许在协议超时内结束。恢复也会重新探测自动暂停的目标。")
            .ProducesProblem(409);
        return api;
    }

    private static Results<Ok<OutputControlState>, ValidationProblem, ProblemHttpResult> Put(
        long connectionId, OutputControlRequest request, OutputControlStore store, HttpContext context)
    {
        try
        {
            return TypedResults.Ok(store.Write(connectionId, request, context.User.FindFirstValue(SessionClaims.AccountName), SourceAddress.Of(context)));
        }
        catch (ArgumentException exception)
        {
            return ApiProblems.Validation(new Dictionary<string, string[]> { ["control"] = [exception.Message] });
        }
        catch (OutputControlConflictException)
        {
            return ApiProblems.Problem(409, "delivery.controlConflict", "目标控制版本已变化，请重新读取后操作。");
        }
    }
}
