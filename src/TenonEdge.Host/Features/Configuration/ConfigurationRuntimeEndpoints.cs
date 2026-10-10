using System.Security.Claims;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.HttpResults;
using TenonEdge.Host.Bootstrap;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Security;
using TenonEdge.Runtime.Diagnostics;

namespace TenonEdge.Host.Features.Configuration;

internal sealed record ConfigurationRuntimeState(RuntimeCoordinatorState State, string? Reason, long? EffectiveRevision,
    long? PendingRevision, string? PlanHash, bool CanRetry, string? ErrorCode);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ConfigurationRetryRequest(long ExpectedEffectiveRevision, string ExpectedPlanHash, bool Confirmed);

internal static class ConfigurationRuntimeEndpoints
{
    public static RouteGroupBuilder MapConfigurationRuntimeEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/configuration/runtime").WithTags("Configuration");
        group.MapGet("/", Read).WithName("getConfigurationRuntimeState")
            .WithSummary("查看持久有效版本与运行失败状态；损坏快照不视为无配置，不改变配置或运行时")
            .ProducesProblem(429);
        group.MapPost("/retry", Retry).WithName("retryConfigurationRuntime")
            .RequireAuthorization(AuthorizationPolicies.Configurator).WithMetadata(new RequestBodyLimit(4096))
            .WithSummary("确认后清零启动保护次数并重试当前有效快照；不应用草稿或生成版本，按操作 ID 查询且禁止自动重试")
            .ProducesProblem(403).ProducesProblem(409).ProducesProblem(413).ProducesProblem(429);
        return api;
    }

    private static Results<Ok<ConfigurationRuntimeState>, ProblemHttpResult> Read(RuntimeVersionStore versions,
        RuntimeCoordinator coordinator, PreviewOperationBudget budget, HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        using var admission = ConnectionTestAdmission.TryAcquire(context.User, budget, out var failure);
        if (failure is not null)
        { return failure; }
        var status = coordinator.Status;
        var pointers = versions.ReadPointers();
        string? hash = null;
        string? code = null;
        if (pointers.EffectiveRevision is { } revision)
        {
            try
            {
                var snapshot = versions.Load(revision) ?? throw new KeyNotFoundException();
                hash = RuntimeSnapshotDocument.ReadPlan(snapshot).ContentHash;
            }
            catch (Exception error) when (error is RuntimePlanDocumentException or KeyNotFoundException)
            { code = "configuration.invalidSnapshot"; }
        }
        else
        { code = "configuration.noEffectiveVersion"; }
        return TypedResults.Ok(new ConfigurationRuntimeState(status.State, status.Reason, pointers.EffectiveRevision,
            pointers.PendingRevision, hash, status.State == RuntimeCoordinatorState.RunFailed && hash is not null &&
            pointers.PendingRevision is null && coordinator.ApplicationStatus is not { FinishedAt: null }, code));
    }

    private static Results<Accepted<RuntimeApplicationStatus>, ValidationProblem, ProblemHttpResult> Retry(ConfigurationRetryRequest request,
        RuntimeVersionStore versions, RuntimeCoordinator coordinator, PreviewOperationBudget budget, HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        using var admission = ConnectionTestAdmission.TryAcquire(context.User, budget, out var failure);
        if (failure is not null)
        { return failure; }
        try
        {
            if (!request.Confirmed || request.ExpectedEffectiveRevision <= 0 || request.ExpectedPlanHash is not { Length: 71 } hash ||
                !hash.StartsWith("sha256:", StringComparison.Ordinal) || hash.Skip(7).Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            { throw new ArgumentException("须明确确认当前有效版本及其计划哈希。", nameof(request)); }
            _ = versions.ReadRetryCheckpoint(request.ExpectedEffectiveRevision, hash);
            var operation = coordinator.BeginRetry(request.ExpectedEffectiveRevision, hash,
                context.User.FindFirstValue(SessionClaims.AccountName)!, SourceAddress.Of(context), context.RequestAborted);
            return TypedResults.Accepted($"{ManagementApi.RoutePrefix}/configuration/operations/{operation.Id}", operation);
        }
        catch (RuntimeApplicationBusyException error)
        {
            return TypedResults.Problem(statusCode: 409, title: "已有配置生命周期操作正在执行；请查询该操作，不要重试。",
                extensions: new Dictionary<string, object?> { ["code"] = "runtime.applicationInProgress", ["operationId"] = error.Application.Id, ["account"] = error.Application.Account });
        }
        catch (RuntimeApplicationUnavailableException) { return ApiProblems.Problem(409, "runtime.retryUnavailable", "仅运行失败状态接受人工重试，未开始操作。"); }
        catch (DraftWriteException error) { return ApiProblems.Problem(409, error.Code, "有效版本已变化或正在应用；未开始重试，请刷新状态。"); }
        catch (KeyNotFoundException) { return ApiProblems.Problem(409, "configuration.invalidSnapshot", "有效快照不存在，不能安全重试。"); }
        catch (RuntimePlanDocumentException) { return ApiProblems.Problem(409, "configuration.invalidSnapshot", "有效快照无效，不能重试；须安全回滚或恢复。"); }
        catch (ArgumentException) { return ApiProblems.Validation(new Dictionary<string, string[]> { ["retry"] = ["须明确确认正整数有效版本及其计划哈希。"] }); }
    }
}
