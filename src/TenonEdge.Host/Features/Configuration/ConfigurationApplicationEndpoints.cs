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
internal sealed record ConfigurationApplicationRequest(string ExpectedVersion, long? ExpectedEffectiveRevision,
    string ExpectedPlanHash, bool Confirmed, string? ChangeDescription = null);

internal static class ConfigurationApplicationEndpoints
{
    public static RouteGroupBuilder MapConfigurationApplicationEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/configuration").WithTags("Configuration")
            .RequireAuthorization(AuthorizationPolicies.Configurator).WithMetadata(new RequestBodyLimit(8192));
        group.MapPost("/apply", Apply).WithName("applyConfigurationDraft")
            .WithSummary("确认并提交当前草稿的应用操作；202 仅表示已接管，请按操作 ID 查询，禁止自动重试")
            .ProducesProblem(403).ProducesProblem(409).ProducesProblem(413).ProducesProblem(429);
        group.MapPost("/versions/{revision:long}/rollback", Rollback).WithName("rollbackConfigurationVersion")
            .WithSummary("确认并提交人工回滚操作；202 不表示已生效，请按操作 ID 查询，禁止自动重试")
            .ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(413).ProducesProblem(429);
        return api;
    }

    private static Results<Accepted<RuntimeApplicationStatus>, ValidationProblem, ProblemHttpResult> Apply(ConfigurationApplicationRequest request,
        RuntimeVersionStore versions, RuntimeCoordinator coordinator, PreviewOperationBudget budget, HttpContext context) =>
        Start(null, request, versions, coordinator, budget, context);

    private static Results<Accepted<RuntimeApplicationStatus>, ValidationProblem, ProblemHttpResult> Rollback(long revision, ConfigurationApplicationRequest request,
        RuntimeVersionStore versions, RuntimeCoordinator coordinator, PreviewOperationBudget budget, HttpContext context) =>
        Start(revision, request, versions, coordinator, budget, context);

    private static Results<Accepted<RuntimeApplicationStatus>, ValidationProblem, ProblemHttpResult> Start(long? revision, ConfigurationApplicationRequest request,
        RuntimeVersionStore versions, RuntimeCoordinator coordinator, PreviewOperationBudget budget, HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        using var admission = ConnectionTestAdmission.TryAcquire(context.User, budget, out var failure);
        if (failure is not null)
        { return failure; }
        try
        {
            if (!request.Confirmed || request.ExpectedEffectiveRevision is <= 0 || request.ExpectedPlanHash is not { Length: 71 } hash ||
                !hash.StartsWith("sha256:", StringComparison.Ordinal) || hash.Skip(7).Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            { throw new ArgumentException("须明确确认已查看的配置清单。"); }
            var description = ConfigurationChangeDescription.Normalize(request.ChangeDescription);
            var review = revision is { } target ? versions.ReviewRollback(target, request.ExpectedVersion, context.RequestAborted)
                : versions.ReviewDraft(request.ExpectedVersion, context.RequestAborted);
            if (!review.PreflightPassed)
            { return ApiProblems.Problem(409, "configuration.preflightFailed", "配置清单存在错误、目标阻断或不可比较的旧快照；未开始操作，请刷新清单。"); }
            if (review.EffectiveRevision != request.ExpectedEffectiveRevision || review.PlanHash != hash)
            { return ApiProblems.Problem(409, "configuration.reviewChanged", "确认清单的有效版本或运行计划已变化；未开始操作，请刷新清单。"); }
            var expectation = new RuntimeApplicationExpectation(review.EffectiveRevision, hash);
            var account = ManagementIdentity.Audit(context.User);
            var operation = revision is { } value
                ? coordinator.BeginRollback(value, request.ExpectedVersion, expectation, account, SourceAddress.Of(context), description, context.RequestAborted)
                : coordinator.BeginApplyDraft(request.ExpectedVersion, expectation, account, SourceAddress.Of(context), description, context.RequestAborted);
            return TypedResults.Accepted($"{ManagementApi.RoutePrefix}/configuration/operations/{operation.Id}", operation);
        }
        catch (RuntimeApplicationBusyException error)
        {
            return TypedResults.Problem(statusCode: 409, title: "已有应用/回滚正在执行；请查询该操作，不要自动重试。",
                extensions: new Dictionary<string, object?> { ["code"] = "runtime.applicationInProgress", ["operationId"] = error.Application.Id, ["account"] = error.Application.Account });
        }
        catch (RuntimeApplicationUnavailableException) { return ApiProblems.Problem(409, "runtime.applicationUnavailable", "当前运行状态不接受应用/回滚，未开始操作。"); }
        catch (DraftWriteException error) { return ApiProblems.Problem(409, error.Code, "草稿、有效版本或连接凭证已变化，未开始操作；请刷新清单。"); }
        catch (RuntimeVersionConflictException error) { return ApiProblems.Problem(409, error.Code, "版本已变化，未开始操作；请刷新清单。"); }
        catch (KeyNotFoundException) { return ApiProblems.Problem(404, "configuration.versionNotFound", "回滚版本不存在或已清理，未开始操作。"); }
        catch (NotSupportedException) { return ApiProblems.Problem(409, "configuration.unsupportedSnapshot", "旧快照无法安全确认，未开始操作。"); }
        catch (RuntimePlanDocumentException) { return ApiProblems.Problem(409, "configuration.invalidSnapshot", "配置快照无效或超过大小限制，未开始操作。"); }
        catch (ArgumentException) { return ApiProblems.Validation(new Dictionary<string, string[]> { ["application"] = ["须明确确认有效的草稿版本、计划哈希和目标版本；说明最多 1000 字符且不能含非法控制字符。"] }); }
    }
}
