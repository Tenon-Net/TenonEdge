using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.HttpResults;
using TenonEdge.Host.Bootstrap;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Features.Tokens;
using TenonEdge.Host.Security;
using TenonEdge.Runtime.Diagnostics;

namespace TenonEdge.Host.Features.Configuration;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ConfigurationRecoveryReviewRequest([property: JsonRequired] string? ExpectedVersion);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ConfigurationRecoveryRequest(string ExpectedVersion, string ConfirmationHash, bool Confirmed,
    bool PreserveDraftConfirmed, string? ChangeDescription = null);

internal static class ConfigurationRecoveryEndpoints
{
    public static RouteGroupBuilder MapConfigurationRecoveryEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/configuration/versions").WithTags("Configuration")
            .RequireAuthorization(AuthorizationPolicies.Administrator).WithMetadata(new RequestBodyLimit(8192));
        group.MapPost("/{revision:long}/recovery-review", Review).WithName("reviewCorruptSnapshotRecovery")
            .WithSummary("管理员预检损坏有效快照恢复；完整历史、当前凭证和冻结目标核对，不清空或替换草稿")
            .ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(429);
        group.MapPost("/{revision:long}/recover", Confirm).WithName("recoverCorruptSnapshot")
            .WithSummary("管理员明确确认后生成新有效版本，保留草稿；202仅接管，按操作ID查询且禁止自动重试")
            .ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(429);
        return api;
    }

    private static async Task<Results<Ok<ConfigurationRecoveryReview>, ValidationProblem, ProblemHttpResult>> Review(long revision,
        ConfigurationRecoveryReviewRequest request, RuntimeCoordinator coordinator, RuntimeVersionStore versions, PreviewOperationBudget budget, HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        using var admission = ConnectionTestAdmission.TryAcquire(context.User, budget, out var failure);
        if (failure is not null)
        { return failure; }
        try
        {
            var draftVersion = request.ExpectedVersion ?? versions.ReadRecoveryDraftVersion();
            Validate(revision, draftVersion);
            var captured = await coordinator.ReviewRecoveryAsync(revision, draftVersion, ManagementActor.Capture(context), context.RequestAborted);
            return TypedResults.Ok(captured.Review);
        }
        catch (ArgumentException) { return Invalid(); }
        catch (Exception error) when (Expected(error)) { return Problem(error); }
    }

    private static async Task<Results<Accepted<RuntimeApplicationStatus>, ValidationProblem, ProblemHttpResult>> Confirm(long revision,
        ConfigurationRecoveryRequest request, RuntimeCoordinator coordinator, PreviewOperationBudget budget, HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        using var admission = ConnectionTestAdmission.TryAcquire(context.User, budget, out var failure);
        if (failure is not null)
        { return failure; }
        try
        {
            Validate(revision, request.ExpectedVersion);
            if (!request.Confirmed || !request.PreserveDraftConfirmed || request.ConfirmationHash is not { Length: 71 })
            { throw new ArgumentException("须确认恢复预检和保留草稿。"); }
            var captured = await coordinator.ReviewRecoveryAsync(revision, request.ExpectedVersion, ManagementActor.Capture(context), context.RequestAborted);
            if (!captured.Review.CanConfirm || captured.Review.ConfirmationHash != request.ConfirmationHash)
            { return ApiProblems.Problem(409, "configuration.recoveryReviewChanged", "恢复清单已变化或仍有阻断；请重新预检，未执行恢复。"); }
            var operation = coordinator.BeginRecovery(captured.Authorization, ManagementIdentity.Audit(context.User), request.ChangeDescription, context.RequestAborted);
            return TypedResults.Accepted($"{ManagementApi.RoutePrefix}/configuration/operations/{operation.Id}", operation);
        }
        catch (ArgumentException) { return Invalid(); }
        catch (Exception error) when (Expected(error)) { return Problem(error); }
    }

    private static void Validate(long revision, string version)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(revision);
        if (string.IsNullOrWhiteSpace(version) || version.Length > 64)
        { throw new ArgumentException("草稿版本无效。"); }
    }
    private static ValidationProblem Invalid() => ApiProblems.Validation(new Dictionary<string, string[]>
    { ["recovery"] = ["须提供正整数历史版本、有效草稿版本、完整预检哈希及两项明确确认；说明最多1000字符。"] });
    private static bool Expected(Exception error) => error is RuntimeApplicationUnavailableException or RuntimeApplicationBusyException or
        DraftWriteException or RuntimeVersionConflictException or RuntimePlanDocumentException or KeyNotFoundException or NotSupportedException or ApiTokenException;
    private static ProblemHttpResult Problem(Exception error) => ApiProblems.Problem(error is ApiTokenException ? 401 : error is KeyNotFoundException ? 404 : 409,
        error switch
        {
            DraftWriteException draft => draft.Code,
            RuntimeVersionConflictException version => version.Code,
            RuntimePlanDocumentException => "configuration.invalidSnapshot",
            KeyNotFoundException => "configuration.versionNotFound",
            NotSupportedException => "configuration.unsupportedSnapshot",
            ApiTokenException => "auth.actorChanged",
            _ => "configuration.recoveryUnavailable",
        }, "当前状态、历史快照或身份不满足恢复前提；未开始恢复，请刷新后核对。 ");
}
