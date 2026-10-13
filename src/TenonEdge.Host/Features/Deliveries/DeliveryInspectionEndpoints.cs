using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Data.Sqlite;
using TenonEdge.Host.Bootstrap;
using TenonEdge.Host.Security;
using TenonEdge.Runtime.Deliveries;

namespace TenonEdge.Host.Features.Deliveries;

internal static class DeliveryInspectionEndpoints
{
    public static RouteGroupBuilder MapDeliveryInspectionEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/deliveries").WithTags("Deliveries");
        group.MapGet("", List).WithName("getDeliveryRecords").WithSummary("筛选失败区或隔离区元数据，不读取正文")
            .WithDescription("state 必须是 failed 或 quarantined；按 seq 升序，每页最多 100。deviceKey 精确匹配；reason 为区分大小写的字面子串。from（包含）/to（不包含）筛选首次入队时间。独立页面不是跨页一致快照，期间记录可能过期或被操作。")
            .ProducesProblem(400).ProducesProblem(503);
        group.MapGet("/{deliveryId:guid}/body", Body).RequireAuthorization(AuthorizationPolicies.Configurator)
            .WithName("getDeliveryBody").WithSummary("按稳定投递编号查看最多 64 KiB 正文")
            .WithDescription("指定 failed 或 quarantined 区域；记录删除或状态变化返回 404。非 UTF-8 前缀返回显式 Base64。只读角色禁止访问；不返回头、凭证或指纹。responseSummary 为上次记录的 HTTP 响应诊断：完整有界正文经凭证回显检查后最多保留 1024 UTF-8 字节；仅按正文权限提供，不进入列表/日志/导出，不能保证识别未知秘密或任意编码。旧记录或未取得响应时为 null，隐藏/超限/不可读取等状态不改变投递分类。")
            .ProducesProblem(400).ProducesProblem(403).ProducesProblem(404).ProducesProblem(503);
        return api;
    }

    private static Results<Ok<DeliveryInspectionPage>, ProblemHttpResult> List(string? state, long? connectionId, string? deviceKey,
        string? reason, DateTimeOffset? from, DateTimeOffset? to, long? after, int? limit, DeliveryCache cache, HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        try
        {
            return TypedResults.Ok(cache.Inspect(new(ParseState(state), connectionId, deviceKey, reason, from, to, after ?? 0, limit ?? 100)));
        }
        catch (ArgumentException) { return Invalid(); }
        catch (Exception exception) when (exception is SqliteException or IOException or UnauthorizedAccessException or DeliveryStoreException)
        { return Unavailable(); }
    }

    private static Results<Ok<DeliveryBodyPreview>, ProblemHttpResult> Body(Guid deliveryId, string? state, DeliveryCache cache, HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        try
        {
            var result = cache.InspectBody(deliveryId, ParseState(state));
            return result is null ? ApiProblems.Problem(404, "delivery.recordNotFound", "该区域中的记录已不存在或已改变状态，请刷新列表。") : TypedResults.Ok(result);
        }
        catch (ArgumentException) { return Invalid(); }
        catch (Exception exception) when (exception is SqliteException or IOException or UnauthorizedAccessException or DeliveryStoreException)
        { return Unavailable(); }
    }

    private static DeliveryState ParseState(string? state) => state switch
    {
        "failed" => DeliveryState.Failed,
        "quarantined" => DeliveryState.Quarantined,
        _ => throw new ArgumentException("未知故障区域。", nameof(state)),
    };
    private static ProblemHttpResult Invalid() => ApiProblems.Problem(400, "delivery.invalidQuery", "故障记录查询参数无效，请核对区域、筛选条件和分页范围。");
    private static ProblemHttpResult Unavailable() => ApiProblems.Problem(503, "delivery.queueUnavailable", "队列暂不可读取，不能当作零记录。");
}
