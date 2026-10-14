using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using TenonEdge.Host.Bootstrap;
using TenonEdge.Host.Security;

namespace TenonEdge.Host.Features.Connections;

internal static class OutboundPolicyEndpoints
{
    public static RouteGroupBuilder MapOutboundPolicyEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/system").WithTags("System");
        group.MapGet("/outbound-policy", Get)
            .WithName("getOutboundPolicy").WithSummary("分别查看网页持久和部署出站规则与本机端口例外")
            .WithDescription("部署参数规则额外生效，不会被本接口删除；deployment为null表示部署规则无效、出站拒绝，不可当成空白名单。本机管理端口和危险地址始终禁止。");
        group.MapPut("/outbound-policy", Put).RequireAuthorization(AuthorizationPolicies.Administrator)
            .WithName("writeOutboundPolicy").WithSummary("管理员更新出站白名单并记录审计")
            .WithDescription("仅影响之后建立的连接；已建立的连接不会被本次更新强制中断。")
            .ProducesProblem(409);
        return api;
    }

    private static Ok<OutboundPolicyView> Get(OutboundPolicyStore store, IConfiguration configuration, HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        var stored = store.Read(); // 无法读取持久规则时保留错误，不返回空表或仅部署规则。
        OutboundDeploymentRules? deployment;
        try
        { deployment = OutboundDeploymentRules.Read(configuration); }
        catch (Exception exception) when (exception is ArgumentException or FormatException or InvalidOperationException)
        { deployment = null; }
        return TypedResults.Ok(new OutboundPolicyView(stored.Version, stored.Networks, stored.LocalPorts, deployment));
    }

    private static Results<Ok<OutboundPolicyState>, ValidationProblem, ProblemHttpResult> Put(
        OutboundPolicyRequest request, OutboundPolicyStore store, HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        try
        {
            return TypedResults.Ok(store.Write(request, ManagementIdentity.Audit(context.User), SourceAddress.Of(context)));
        }
        catch (ArgumentException exception)
        {
            return ApiProblems.Validation(new Dictionary<string, string[]> { ["policy"] = [exception.Message] });
        }
        catch (OutboundPolicyConflictException)
        {
            return ApiProblems.Problem(409, "system.outboundPolicyConflict", "出站规则版本已变化，请重新读取后操作。");
        }
    }
}

internal sealed record OutboundPolicyView(string? Version, OutboundNetworkGrant[] Networks, int[] LocalPorts, OutboundDeploymentRules? Deployment);
