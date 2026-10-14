using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Data.Sqlite;
using TenonEdge.Host.Bootstrap;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Security;
using TenonEdge.Runtime.Deliveries;

namespace TenonEdge.Host.Features.Deliveries;

internal static class DeliveryMigrationEndpoints
{
    public static RouteGroupBuilder MapDeliveryMigrationEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/deliveries/migrations").WithTags("Deliveries").RequireAuthorization(AuthorizationPolicies.Administrator);
        group.MapGet("/records", Records).WithName("getMigrationDeliveryRecords").WithSummary("管理员选择同一源目标的待发、失败或隔离记录")
            .WithDescription("仅元数据，每页最多100；不读取正文或身份摘要。翻页不是一致快照，准备时逐条重新核对选择。")
            .ProducesProblem(400).ProducesProblem(403).ProducesProblem(503);
        group.MapPost("/prepare", Prepare).WithName("prepareDeliveryMigration").WithSummary("核对迁移选择、原接收方和有效同类目标")
            .WithDescription("最多1000条，身份元数据总计最多4MiB，超限整批拒绝而不截断。逐条按冻结摘要判定原协议，不从当前源连接猜测。返回账户/客户端证书是否已记录及配额缺口；准备不迁移、不审计开始、不暂停发送者。全网关一份确认清单，属于创建它的登录会话，有效期15分钟。")
            .ProducesProblem(400).ProducesProblem(403).ProducesProblem(409).ProducesProblem(503);
        group.MapGet("/preparation", Current).WithName("getDeliveryMigrationPreparation").WithSummary("恢复当前登录会话尚未消费的迁移确认清单")
            .ProducesProblem(403);
        group.MapDelete("/preparation/{id:guid}", Cancel).WithName("cancelDeliveryMigrationPreparation").WithSummary("取消本登录会话的迁移准备，不改变队列")
            .ProducesProblem(403).ProducesProblem(404).ProducesProblem(409);
        group.MapPost("/{id:guid}/confirm", Confirm).WithName("confirmDeliveryMigration").WithSummary("确认同一数据所有者后执行冻结选择的原子迁移")
            .WithDescription("sameOwnerConfirmed必须为true。服务器消费原确认清单，复核有效版本和目标身份；双发送者静默后再核对记录代次与完整配额。未ACK记录保持原正文、编号、主题/路径、头和FIFO，失败记录仍在失败区。取消等待不取消后台执行；响应丢失只按操作编号查询，不重放确认。")
            .ProducesProblem(400).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(503);
        group.MapGet("/{id:guid}", Outcome).WithName("getDeliveryMigrationOutcome").WithSummary("管理员按操作编号查询持久迁移结果")
            .WithDescription("开始/结果与启动恢复由持久迁移记录和queue.db收据核对；completed表示已迁移，不表示已发送。404不是迁移失败证明，可能尚未登记开始。")
            .ProducesProblem(403).ProducesProblem(404);
        return api;
    }

    private static Results<Ok<DeliveryInspectionPage>, ProblemHttpResult> Records(long connectionId, string state, long? after, int? limit,
        DeliveryCache cache, HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        try
        {
            if (connectionId is < 1 or > 9007199254740991L)
            { throw new ArgumentException("源连接无效。"); }
            var area = state switch
            {
                "pending" => DeliveryState.Pending,
                "failed" => DeliveryState.Failed,
                "quarantined" => DeliveryState.Quarantined,
                _ => throw new ArgumentException("区域无效。")
            };
            return TypedResults.Ok(cache.InspectForMigration(new(area, connectionId, After: after ?? 0, Limit: limit ?? 100)));
        }
        catch (Exception error) when (Handled(error)) { return Problem(error); }
    }

    private static Results<Ok<DeliveryMigrationReview>, ProblemHttpResult> Prepare(DeliveryMigrationPrepareRequest request,
        DeliveryMigrationPreparation preparation, HttpContext context, CancellationToken token)
    {
        context.Response.Headers.CacheControl = "no-store";
        try
        { return TypedResults.Ok(preparation.Prepare(request, Actor(context), token)); }
        catch (Exception error) when (Handled(error)) { return Problem(error); }
    }

    private static Results<Ok<DeliveryMigrationCurrent>, ProblemHttpResult> Current(DeliveryMigrationPreparation preparation, HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        try
        { return TypedResults.Ok(new DeliveryMigrationCurrent(preparation.Current(Actor(context).Owner))); }
        catch (Exception error) when (Handled(error)) { return Problem(error); }
    }

    private static Results<NoContent, ProblemHttpResult> Cancel(Guid id, DeliveryMigrationPreparation preparation, HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        try
        { preparation.Cancel(id, Actor(context).Owner); return TypedResults.NoContent(); }
        catch (Exception error) when (Handled(error)) { return Problem(error); }
    }

    private static async Task<Results<Ok<DeliveryMigrationConfirmationResult>, ProblemHttpResult>> Confirm(Guid id,
        DeliveryMigrationConfirmation request, DeliveryMigrationPreparation preparation, HttpContext context, CancellationToken token)
    {
        context.Response.Headers.CacheControl = "no-store";
        try
        {
            var result = await preparation.Confirm(id, request.SameOwnerConfirmed, Actor(context), token);
            if (result.Committed)
            { return TypedResults.Ok(new DeliveryMigrationConfirmationResult(id, result.AffectedRecords)); }
            return TypedResults.Problem(statusCode: result.Status == DeliveryWriteStatus.Rejected ? 409 : 503,
                title: "迁移未完成或结果不可确定。按操作编号查询并重新核对，不自动重放确认。",
                extensions: new Dictionary<string, object?> { ["code"] = result.Error ?? "delivery.migrationOutcomeUnknown", ["migrationQuota"] = result.MigrationQuota });
        }
        catch (Exception error) when (Handled(error)) { return Problem(error); }
    }

    private static Results<Ok<DeliveryMigrationOutcome>, ProblemHttpResult> Outcome(Guid id, DeliveryMigrationStore migrations, HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        var row = migrations.Read(id);
        return row is null ? ApiProblems.Problem(404, "delivery.migrationNotFound", "尚未发现该编号的持久迁移记录，请核对编号与当前状态。")
            : TypedResults.Ok(new DeliveryMigrationOutcome(id, row.Status, row.RequestedRecords, row.AffectedRecords, row.MigrationId, row.ErrorCode));
    }

    internal static DeliveryMigrationActor Actor(HttpContext context)
    {
        var owner = ManagementIdentity.OwnerKey(context.User);
        var account = ManagementIdentity.Audit(context.User);
        if (owner is null || account.Name is null)
        { throw new DeliveryMigrationRequestException(403, "delivery.migrationSessionRequired"); }
        return new(owner, account, SourceAddress.Of(context));
    }

    private static bool Handled(Exception error) => error is ArgumentException or DeliveryMigrationRequestException or DeliveryMigrationSelectionException or
        DeliveryMigrationUnavailableException or RuntimePlanDocumentException or SqliteException or IOException or UnauthorizedAccessException or DeliveryStoreException;
    private static ProblemHttpResult Problem(Exception error) => error switch
    {
        DeliveryMigrationRequestException request => ApiProblems.Problem(request.StatusCode, request.Code, "迁移准备或确认不可用，请重新核对选择、当前版本和接收方。"),
        DeliveryMigrationSelectionException selection => ApiProblems.Problem(409, selection.Code, "选择已变化或身份摘要超出预算，请缩小选择并重新读取。"),
        DeliveryMigrationUnavailableException => ApiProblems.Problem(409, DeliveryMigrationErrors.Unavailable, "有其他运行操作或有效版本已变化，请重新准备。"),
        ArgumentException => ApiProblems.Problem(400, "delivery.invalidMigrationRequest", "请选择1至1000条不同记录、另一个目标，并明确确认同一数据所有者。"),
        RuntimePlanDocumentException => ApiProblems.Problem(503, "configuration.invalidSnapshot", "有效快照不可读取，不能以当前草稿代替。"),
        _ => ApiProblems.Problem(503, "delivery.queueUnavailable", "队列暂不可读取，不能当作零记录。"),
    };
}

internal sealed record DeliveryMigrationCurrent(DeliveryMigrationReview? Preparation);
internal sealed record DeliveryMigrationConfirmation(bool SameOwnerConfirmed);
internal sealed record DeliveryMigrationConfirmationResult(Guid OperationId, int MigratedRecords);
internal sealed record DeliveryMigrationOutcome(Guid OperationId, string Status, int? RequestedRecords, int? AffectedRecords, long? MigrationId, string? ErrorCode);
