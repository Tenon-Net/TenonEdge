using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Host.Bootstrap;
using TenonEdge.Host.Features.Audit;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Security;

namespace TenonEdge.Host.Features.Deliveries;

internal static class OutputControlEndpoints
{
    public static RouteGroupBuilder MapOutputControlEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/connections/{connectionId:long}").WithTags("Deliveries");
        group.MapGet("/delivery-control", (long connectionId, OutputControlStore store, HttpContext context) =>
            {
                context.Response.Headers.CacheControl = "no-store";
                return TypedResults.Ok(store.Read(connectionId));
            })
            .WithName("getOutputControl").WithSummary("查看目标手动暂停状态");
        group.MapPut("/delivery-control", Put).RequireAuthorization(AuthorizationPolicies.Administrator)
            .WithName("writeOutputControl").WithSummary("确认后手动暂停或恢复目标")
            .WithDescription("暂停持久保存；已经在途的一条发送允许在协议超时内结束。恢复也会重新探测自动暂停的目标。")
            .ProducesProblem(409);
        group.MapPost("/retry-now", Retry).RequireAuthorization(AuthorizationPolicies.Configurator)
            .WithName("retryOutputNow").WithSummary("请求跳过当前退避并探测一条，不解除暂停")
            .WithDescription("无需二次确认；绑定运行版本及当前退避窗口，重复信号合并，不排队到下一次退避。202仅表示已接受内存信号，不承诺已发送或收到业务确认。手动/自动暂停、停止、窗口过期或版本切换拒绝。")
            .ProducesProblem(400).ProducesProblem(403).ProducesProblem(409);
        return api;
    }

    private static Results<Accepted, ProblemHttpResult> Retry(long connectionId, OutputRetryRequest request,
        RuntimeCoordinator coordinator, AuditTrail audit, HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (connectionId is < 1 or > 9007199254740991L || request.Revision < 1 ||
            !Guid.TryParseExact(request.BackoffVersion, "N", out _))
        { return ApiProblems.Problem(400, "delivery.invalidRetry", "请提供有效目标、运行版本及当前退避窗口。"); }
        if (!coordinator.Runtime.RequestOutputRetry(connectionId, request.Revision, request.BackoffVersion!))
        { return ApiProblems.Problem(409, "delivery.retryUnavailable", "目标不处于该退避窗口或已暂停/切换版本，请重新查询；暂停须由管理员恢复。"); }
        audit.AppendOrLog(new("delivery.retryNow", AuditResults.Success, context.User.FindFirstValue(SessionClaims.AccountName),
            SourceAddress.Of(context), connectionId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "已接受跳过当前退避的内存信号，不代表报文发送成功；不解除手动或自动暂停。"));
        return TypedResults.Accepted((string?)null);
    }

    private static Results<Ok<OutputControlState>, ValidationProblem, ProblemHttpResult> Put(
        long connectionId, OutputControlRequest request, OutputControlStore store, HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
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

internal sealed record OutputRetryRequest(long Revision, string? BackoffVersion);
