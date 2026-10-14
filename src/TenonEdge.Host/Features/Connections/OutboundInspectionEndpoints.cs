using System.Net.Sockets;
using Microsoft.AspNetCore.Http.HttpResults;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Host.Bootstrap;
using TenonEdge.Host.Security;
using TenonEdge.Runtime.Diagnostics;

namespace TenonEdge.Host.Features.Connections;

internal static class OutboundInspectionEndpoints
{
    public static RouteGroupBuilder MapOutboundInspectionEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/connections/inspect-outbound", Inspect).WithTags("Connections")
            .RequireAuthorization(AuthorizationPolicies.Configurator).WithName("inspectConnectionOutbound")
            .WithSummary("解析未保存目标并按当前出站规则检查，不连接或修改白名单")
            .WithDescription("DNS限5秒/32地址；与测试预览共用并发预算。仅公网拒绝生成/32或/128主机CIDR建议；管理员仍须确认后调用版本化白名单写接口。检查不保证之后DNS/规则不变，实际连接仍逐次校验。")
            .WithMetadata(new RequestBodyLimit(4096)).ProducesProblem(403).ProducesProblem(413)
            .ProducesProblem(429).ProducesProblem(503).ProducesProblem(504);
        return api;
    }

    private static async Task<Results<Ok<OutboundInspectionResult>, ValidationProblem, ProblemHttpResult>> Inspect(
        OutboundInspectionRequest request, OutboundPolicyInspector inspector, PreviewOperationBudget budget, HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        using var admission = ConnectionTestAdmission.TryAcquire(context.User, budget, out var failure);
        if (failure is not null)
        { return failure; }
        try
        { return TypedResults.Ok(await inspector.InspectAsync(request, context.RequestAborted)); }
        catch (ArgumentException exception)
        { return ApiProblems.Validation(new Dictionary<string, string[]> { ["target"] = [exception.Message] }); }
        catch (OutboundInspectionLimitException)
        { return ApiProblems.Problem(413, "outbound.addressLimit", "解析地址超过32个，未截取部分地址冒充完整检查。"); }
        catch (SocketException)
        { return ApiProblems.Problem(503, "outbound.dnsFailed", "目标地址解析失败，未保存任何规则。"); }
        catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
        { return ApiProblems.Problem(504, "outbound.dnsTimeout", "地址解析超时，未保存任何规则。"); }
        catch (OutboundAccessDeniedException)
        { return ApiProblems.Problem(503, "outbound.policyInvalid", "出站规则无效，不能提供授权建议，请先检查系统设置。"); }
        catch (IOException)
        { return ApiProblems.Problem(503, "outbound.policyUnavailable", "不能读取出站规则或本机地址，未按空白名单处理。"); }
    }
}
