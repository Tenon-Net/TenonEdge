using System.Security.Claims;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.HttpResults;
using TenonEdge.Host.Bootstrap;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Security;
using TenonEdge.Runtime.Diagnostics;

namespace TenonEdge.Host.Features.Configuration;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ConfigurationDiscardRequest(string ExpectedVersion, long? ExpectedEffectiveRevision, bool Confirmed);

internal static class ConfigurationDraftEndpoints
{
    public static RouteGroupBuilder MapConfigurationDraftEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/configuration/draft").WithTags("Configuration");
        group.MapGet("/", Summary).WithName("getConfigurationDraftSummary")
            .WithSummary("查看全局草稿相对有效配置的对象修改数；不编译或应用，旧版本无法比较时数量为未知")
            .ProducesProblem(403).ProducesProblem(409).ProducesProblem(429);
        group.MapPost("/discard", Discard).WithName("discardConfigurationDraft")
            .RequireAuthorization(AuthorizationPolicies.Configurator).WithMetadata(new RequestBodyLimit(4096))
            .WithSummary("二次确认后放弃全部未应用修改；不改变运行配置、队列或当前凭证，不自动重试")
            .ProducesProblem(403).ProducesProblem(409).ProducesProblem(413).ProducesProblem(429);
        return api;
    }

    private static Results<Ok<ConfigurationDraftSummary>, ValidationProblem, ProblemHttpResult> Summary(
        RuntimeVersionStore versions, PreviewOperationBudget budget, HttpContext context) =>
        Execute(() => versions.ReadDraftSummary(context.RequestAborted), budget, context);

    private static Results<Ok<ConfigurationDiscardResult>, ValidationProblem, ProblemHttpResult> Discard(ConfigurationDiscardRequest request,
        RuntimeVersionStore versions, PreviewOperationBudget budget, HttpContext context) => Execute(() =>
        {
            if (!request.Confirmed)
            { throw new ArgumentException("须明确确认放弃全部草稿修改。"); }
            return versions.DiscardDraft(request.ExpectedVersion, request.ExpectedEffectiveRevision,
                context.User.FindFirstValue(SessionClaims.AccountName)!, SourceAddress.Of(context), context.RequestAborted);
        }, budget, context);

    private static Results<Ok<T>, ValidationProblem, ProblemHttpResult> Execute<T>(Func<T> operation, PreviewOperationBudget budget, HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        using var admission = ConnectionTestAdmission.TryAcquire(context.User, budget, out var failure);
        if (failure is not null)
        { return failure; }
        try
        { return TypedResults.Ok(operation()); }
        catch (DraftWriteException error) { return ApiProblems.Problem(409, error.Code, "草稿或有效配置已变化，或配置正在应用；未放弃修改，请刷新后确认。"); }
        catch (NotSupportedException) { return ApiProblems.Problem(409, "configuration.unsupportedSnapshot", "旧版有效配置没有完整草稿，不能安全放弃修改。"); }
        catch (KeyNotFoundException) { return ApiProblems.Problem(409, "configuration.invalidSnapshot", "有效配置快照缺失，不能安全读取或放弃草稿。"); }
        catch (RuntimePlanDocumentException) { return ApiProblems.Problem(409, "configuration.invalidSnapshot", "配置快照无效或超过大小限制，不能安全读取或放弃草稿。"); }
        catch (ArgumentException) { return ApiProblems.Validation(new Dictionary<string, string[]> { ["draft"] = ["请明确确认放弃修改，并提供有效的草稿版本和有效配置版本。"] }); }
    }
}
