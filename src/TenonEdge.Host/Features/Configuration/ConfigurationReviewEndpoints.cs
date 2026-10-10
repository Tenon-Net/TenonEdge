using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.HttpResults;
using TenonEdge.Host.Bootstrap;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Security;
using TenonEdge.Runtime.Diagnostics;

namespace TenonEdge.Host.Features.Configuration;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ConfigurationReviewRequest(string ExpectedVersion);

internal static class ConfigurationReviewEndpoints
{
    public static RouteGroupBuilder MapConfigurationReviewEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/configuration").WithTags("Configuration")
            .RequireAuthorization(AuthorizationPolicies.Configurator).WithMetadata(new RequestBodyLimit(4096));
        group.MapPost("/review", ReviewDraft).WithName("reviewConfigurationDraft")
            .WithSummary("预检当前草稿的配置差异、路线错误和目标阻断；不应用配置")
            .ProducesProblem(403).ProducesProblem(409).ProducesProblem(413).ProducesProblem(429);
        group.MapPost("/versions/{revision:long}/review", ReviewRollback).WithName("reviewConfigurationRollback")
            .WithSummary("预检回滚目标与当前草稿、凭证及积压记录；不执行回滚")
            .ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(413).ProducesProblem(429);
        return api;
    }

    private static Results<Ok<ConfigurationReview>, ValidationProblem, ProblemHttpResult> ReviewDraft(ConfigurationReviewRequest request,
        RuntimeVersionStore versions, PreviewOperationBudget budget, HttpContext context) =>
        Review(() => versions.ReviewDraft(request.ExpectedVersion, context.RequestAborted), budget, context);

    private static Results<Ok<ConfigurationReview>, ValidationProblem, ProblemHttpResult> ReviewRollback(long revision, ConfigurationReviewRequest request,
        RuntimeVersionStore versions, PreviewOperationBudget budget, HttpContext context) =>
        Review(() => versions.ReviewRollback(revision, request.ExpectedVersion, context.RequestAborted), budget, context);

    private static Results<Ok<ConfigurationReview>, ValidationProblem, ProblemHttpResult> Review(Func<ConfigurationReview> read,
        PreviewOperationBudget budget, HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        using var admission = ConnectionTestAdmission.TryAcquire(context.User, budget, out var failure);
        if (failure is not null)
        { return failure; }
        try
        { return TypedResults.Ok(read()); }
        catch (DraftWriteException error) { return ApiProblems.Problem(409, error.Code, "当前草稿、有效版本或连接凭证不满足预检前提；未执行应用或回滚。"); }
        catch (RuntimeVersionConflictException error) { return ApiProblems.Problem(409, error.Code, "版本已变化，请刷新清单；未执行回滚。"); }
        catch (KeyNotFoundException) { return ApiProblems.Problem(404, "configuration.versionNotFound", "版本不存在或已清理；未执行回滚。"); }
        catch (NotSupportedException) { return ApiProblems.Problem(409, "configuration.unsupportedSnapshot", "旧版快照无法确认完整草稿或凭证引用；未执行回滚。"); }
        catch (RuntimePlanDocumentException) { return ApiProblems.Problem(409, "configuration.invalidSnapshot", "配置快照无效或超过大小限制，不能确认应用或回滚。"); }
        catch (ArgumentException) { return ApiProblems.Validation(new Dictionary<string, string[]> { ["review"] = ["请提供有效的草稿版本和正整数目标版本。"] }); }
    }
}
