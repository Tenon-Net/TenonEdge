using Microsoft.AspNetCore.Http.HttpResults;
using TenonEdge.Host.Bootstrap;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Deliveries;
using TenonEdge.Runtime.Deliveries;
using TenonEdge.Runtime.Diagnostics;

namespace TenonEdge.Host.Features.Configuration;

internal static partial class ConfigurationMigrationEndpoints
{
    private static void MapIntentOperations(RouteGroupBuilder group)
    {
        group.MapGet("/intents", IntentPage).WithName("getConfigurationMigrationIntents")
            .WithSummary("管理员按持久版本/连接游标查看迁移意图；只查元数据，不自动重试")
            .ProducesProblem(400).ProducesProblem(403).ProducesProblem(503);
        group.MapGet("/intents/{id:guid}/preflight", InspectIntent).WithName("inspectConfigurationMigrationIntent")
            .WithSummary("只读核对原确认/当前有效接收方与当前完整配额缺口；不是历史拒绝时的配额，也不授权重试")
            .ProducesProblem(403).ProducesProblem(404).ProducesProblem(429).ProducesProblem(503);
        group.MapPost("/intents/{id:guid}/retry/prepare", PrepareIntentRetry).WithName("prepareConfigurationMigrationIntentRetry")
            .WithSummary("管理员重新核对拒绝或旧版未确认意图；不改变状态、不启动迁移")
            .WithDescription("一份会话归属的15分钟清单。新目标来自当前有效计划，不是草稿；缺口非零不可确认。返回id为新尝试编号，确认回复丢失后仅查询deliveries/migrations/{id}。")
            .ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(429).ProducesProblem(503);
        group.MapGet("/intent-retry/preparation", CurrentIntentRetry).WithName("getConfigurationMigrationIntentRetryPreparation")
            .WithSummary("恢复本登录会话尚未消费的意图重试核对清单").ProducesProblem(403);
        group.MapDelete("/intent-retry/preparation/{id:guid}", CancelIntentRetry).WithName("cancelConfigurationMigrationIntentRetryPreparation")
            .WithSummary("取消未消费的核对清单，不取消后台尝试").ProducesProblem(403).ProducesProblem(404).ProducesProblem(409);
        group.MapPost("/intent-retry/{id:guid}/confirm", ConfirmIntentRetry).WithName("confirmConfigurationMigrationIntentRetry")
            .WithSummary("一次确认原/现接收方属于同一所有者，原子登记新审批及尝试；保留全部历史")
            .WithDescription("confirmed及sameOwnerConfirmed须为true。结果未知的started及已完成意图不可重放；不重新选择或扩大旧隔离范围，不解除手动暂停。请求取消仅结束等待，后台和停机所有权归原协调者。")
            .ProducesProblem(400).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(413).ProducesProblem(429).ProducesProblem(503);
    }
    private static Results<Ok<DeliveryMigrationIntentPage>, ProblemHttpResult> IntentPage(string? status, long? connectionId,
        long? afterRevision, long? afterConnection, int? limit, DeliveryMigrationStore store, HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        try
        { return TypedResults.Ok(store.ReadIntentPage(status, connectionId, afterRevision, afterConnection, limit ?? 50)); }
        catch (ArgumentException) { return ApiProblems.Problem(400, "configuration.invalidMigrationQuery", "状态只允许waiting/rejected/completed，页大小1至100；连接为安全正整数，版本/连接游标须成对提供。"); }
        catch (Exception error) when (Handled(error)) { return Problem(error); }
    }
    private static Results<Ok<ConfigurationMigrationIntentInspection>, ProblemHttpResult> InspectIntent(Guid id,
        ConfigurationMigrationIntentService service, PreviewOperationBudget budget, HttpContext context, CancellationToken token)
    {
        context.Response.Headers.CacheControl = "no-store";
        using var admission = ConnectionTestAdmission.TryAcquire(context.User, budget, out var failure);
        if (failure is not null)
        { return failure; }
        try
        { return TypedResults.Ok(service.Inspect(id, token)); }
        catch (Exception error) when (Handled(error)) { return Problem(error); }
    }
    private static Results<Ok<ConfigurationMigrationIntentRetryReview>, ProblemHttpResult> PrepareIntentRetry(Guid id,
        ConfigurationMigrationIntentService service, PreviewOperationBudget budget, HttpContext context, CancellationToken token)
    {
        context.Response.Headers.CacheControl = "no-store";
        using var admission = ConnectionTestAdmission.TryAcquire(context.User, budget, out var failure);
        if (failure is not null)
        { return failure; }
        try
        { return TypedResults.Ok(service.Prepare(id, DeliveryMigrationEndpoints.Actor(context), token)); }
        catch (Exception error) when (Handled(error)) { return Problem(error); }
    }
    private static Results<Ok<ConfigurationMigrationIntentRetryCurrent>, ProblemHttpResult> CurrentIntentRetry(ConfigurationMigrationIntentService service, HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        try
        { return TypedResults.Ok(new ConfigurationMigrationIntentRetryCurrent(service.Current(DeliveryMigrationEndpoints.Actor(context).Owner))); }
        catch (Exception error) when (Handled(error)) { return Problem(error); }
    }
    private static Results<NoContent, ProblemHttpResult> CancelIntentRetry(Guid id, ConfigurationMigrationIntentService service, HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        try
        { service.Cancel(id, DeliveryMigrationEndpoints.Actor(context).Owner); return TypedResults.NoContent(); }
        catch (Exception error) when (Handled(error)) { return Problem(error); }
    }
    private static async Task<Results<Ok<DeliveryMigrationConfirmationResult>, ProblemHttpResult>> ConfirmIntentRetry(Guid id,
        ConfigurationMigrationIntentConfirmation request, ConfigurationMigrationIntentService service, PreviewOperationBudget budget,
        HttpContext context, CancellationToken token)
    {
        context.Response.Headers.CacheControl = "no-store";
        using var admission = ConnectionTestAdmission.TryAcquire(context.User, budget, out var failure);
        if (failure is not null)
        { return failure; }
        try
        {
            var result = await service.Confirm(id, request, DeliveryMigrationEndpoints.Actor(context), token);
            return result.Committed ? TypedResults.Ok(new DeliveryMigrationConfirmationResult(id, result.AffectedRecords))
                : TypedResults.Problem(statusCode: result.Status == DeliveryWriteStatus.Rejected ? 409 : 503,
                    title: "意图重试未完成或结果未知；只按准备编号查询新尝试，不重放确认。",
                    extensions: new Dictionary<string, object?> { ["code"] = result.Error, ["migrationQuota"] = result.MigrationQuota });
        }
        catch (Exception error) when (Handled(error)) { return Problem(error); }
    }
}

internal sealed record ConfigurationMigrationIntentRetryCurrent(ConfigurationMigrationIntentRetryReview? Preparation);
