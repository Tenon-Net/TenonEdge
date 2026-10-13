using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Data.Sqlite;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Host.Bootstrap;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Deliveries;
using TenonEdge.Host.Features.Routes;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Security;
using TenonEdge.Runtime.Deliveries;
using TenonEdge.Runtime.Diagnostics;

namespace TenonEdge.Host.Features.Configuration;

internal static class ConfigurationMigrationEndpoints
{
    public static RouteGroupBuilder MapConfigurationMigrationEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/configuration/migrations").WithTags("Configuration")
            .RequireAuthorization(AuthorizationPolicies.Administrator).WithMetadata(new RequestBodyLimit(32 * 1024));
        group.MapPost("/prepare", Prepare).WithName("prepareConfigurationMigration")
            .WithSummary("管理员核对同连接换地址的原新身份与完整配额；只准备，不提交配置或联网")
            .WithDescription("只允许明确选中的受阻连接。清单包含完整配置差异及逐连接意图编号，属于当前登录会话，有效15分钟；超额不能确认。返回id亦为确认后的应用操作编号，响应丢失只查询，不重放。")
            .ProducesProblem(400).ProducesProblem(403).ProducesProblem(409).ProducesProblem(413).ProducesProblem(429).ProducesProblem(503);
        group.MapGet("/preparation", Current).WithName("getConfigurationMigrationPreparation")
            .WithSummary("恢复当前登录会话的迁移应用核对清单").ProducesProblem(403);
        group.MapDelete("/preparation/{id:guid}", Cancel).WithName("cancelConfigurationMigrationPreparation")
            .WithSummary("取消尚未消费的迁移应用清单，不影响已接管操作或队列").ProducesProblem(403).ProducesProblem(404).ProducesProblem(409);
        group.MapPost("/{id:guid}/confirm", Confirm).WithName("confirmConfigurationMigration")
            .WithSummary("管理员确认同一数据所有者后接管应用；原发送屏障跨配置COMMIT，隔离成功后再激活")
            .WithDescription("confirmed及sameOwnerConfirmed须为true，声明每个所选连接的新旧接收方属于同一数据所有者。一次消费；202仅表示已接管，不保证已提交或迁移。取消请求不取消已接管操作。按准备id查询configuration/operations；重启后按逐连接意图id查询持久结果，404不是未提交证明。")
            .ProducesProblem(400).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(429).ProducesProblem(503);
        group.MapGet("/intents/{id:guid}", Intent).WithName("getConfigurationMigrationIntent")
            .WithSummary("管理员查看持久迁移意图及最近一次尝试；completed不代表已发送")
            .ProducesProblem(403).ProducesProblem(404).ProducesProblem(503);
        return api;
    }

    private static async Task<Results<Ok<ConfigurationMigrationReview>, ProblemHttpResult>> Prepare(ConfigurationMigrationPrepareRequest request,
        ConfigurationMigrationPreparation preparation, PreviewOperationBudget budget, HttpContext context, CancellationToken token)
    {
        context.Response.Headers.CacheControl = "no-store";
        using var admission = ConnectionTestAdmission.TryAcquire(context.User, budget, out var failure);
        if (failure is not null)
        { return failure; }
        try
        { return TypedResults.Ok(await preparation.PrepareAsync(request, DeliveryMigrationEndpoints.Actor(context), token)); }
        catch (Exception error) when (Handled(error)) { return Problem(error); }
    }
    private static Results<Ok<ConfigurationMigrationCurrent>, ProblemHttpResult> Current(ConfigurationMigrationPreparation preparation, HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        try
        { return TypedResults.Ok(new ConfigurationMigrationCurrent(preparation.Current(DeliveryMigrationEndpoints.Actor(context).Owner))); }
        catch (Exception error) when (Handled(error)) { return Problem(error); }
    }
    private static Results<NoContent, ProblemHttpResult> Cancel(Guid id, ConfigurationMigrationPreparation preparation, HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        try
        { preparation.Cancel(id, DeliveryMigrationEndpoints.Actor(context).Owner); return TypedResults.NoContent(); }
        catch (Exception error) when (Handled(error)) { return Problem(error); }
    }
    private static Results<Accepted<RuntimeApplicationStatus>, ProblemHttpResult> Confirm(Guid id, ConfigurationMigrationConfirmation request,
        ConfigurationMigrationPreparation preparation, PreviewOperationBudget budget, HttpContext context, CancellationToken token)
    {
        context.Response.Headers.CacheControl = "no-store";
        using var admission = ConnectionTestAdmission.TryAcquire(context.User, budget, out var failure);
        if (failure is not null)
        { return failure; }
        try
        {
            var operation = preparation.Confirm(id, request, DeliveryMigrationEndpoints.Actor(context), token);
            return TypedResults.Accepted($"{ManagementApi.RoutePrefix}/configuration/operations/{operation.Id}", operation);
        }
        catch (Exception error) when (Handled(error)) { return Problem(error); }
    }
    private static Results<Ok<ConfigurationMigrationIntentOutcome>, ProblemHttpResult> Intent(Guid id, DeliveryMigrationStore store, HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        try
        {
            var row = store.ReadIntent(id);
            if (row is null)
            { return ApiProblems.Problem(404, "configuration.migrationIntentNotFound", "尚未发现该意图；可能尚未提交，请核对应用操作及版本历史，不自动重放。"); }
            var attempt = row.LastOperationId is { } operation ? store.Read(Guid.Parse(operation)) : null;
            return TypedResults.Ok(new ConfigurationMigrationIntentOutcome(id, row.Revision, row.SourceRevision, row.ConnectionId, row.Status,
                OutputRecipientIdentity.Read(row.SourceSummary), OutputRecipientIdentity.Read(row.TargetSummary), row.TargetRecipient,
                attempt?.OperationId, attempt?.Status, attempt?.AffectedRecords, attempt?.ErrorCode));
        }
        catch (Exception error) when (Handled(error)) { return Problem(error); }
    }
    private static bool Handled(Exception error) => error is ArgumentException or DeliveryMigrationRequestException or DraftWriteException or
        RouteTopologyException or RuntimeApplicationBusyException or RuntimeApplicationUnavailableException or RuntimeVersionConflictException or RuntimePlanDocumentException or
        SqliteException or SqlSugar.SqlSugarException or DeliveryStoreException;
    private static ProblemHttpResult Problem(Exception error) => error switch
    {
        DeliveryMigrationRequestException request => ApiProblems.Problem(request.StatusCode, request.Code, "迁移应用清单不可确认，请重新核对当前版本、接收方与配额。"),
        DraftWriteException or RouteTopologyException => ApiProblems.Problem(409, "configuration.migrationReviewChanged", "草稿已变化，请重新准备迁移应用。"),
        RuntimeApplicationBusyException busy => TypedResults.Problem(statusCode: 409, title: "已有应用操作，请查询而非重放确认。",
            extensions: new Dictionary<string, object?> { ["code"] = "runtime.applicationInProgress", ["operationId"] = busy.Application.Id }),
        RuntimeApplicationUnavailableException or RuntimeVersionConflictException => ApiProblems.Problem(409, "runtime.applicationUnavailable", "运行操作或版本已变化，请重新核对。"),
        ArgumentException => ApiProblems.Problem(400, "configuration.invalidMigrationRequest", "须提供有效草稿版本、不同连接及明确的两项确认；说明最多1000字符。"),
        _ => ApiProblems.Problem(503, "configuration.migrationUnavailable", "配置或队列暂不可读写，不按空配置或零记录处理。"),
    };
}

internal sealed record ConfigurationMigrationCurrent(ConfigurationMigrationReview? Preparation);
internal sealed record ConfigurationMigrationIntentOutcome(Guid Id, long Revision, long SourceRevision, long ConnectionId, string Status,
    OutputRecipientIdentity? Source, OutputRecipientIdentity? Target, string TargetRecipient, string? OperationId, string? AttemptStatus,
    int? AffectedRecords, string? ErrorCode);
