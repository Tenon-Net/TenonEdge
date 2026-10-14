using Microsoft.AspNetCore.Http.HttpResults;
using SqlSugar;
using TenonEdge.Host.Bootstrap;
using TenonEdge.Host.Security;
using TenonEdge.Host.Storage;

namespace TenonEdge.Host.Features.Audit;

/// <summary>
/// 审计查询（audit 分组，总设计 §10.1；仅管理员）：按时间、账号、操作、对象筛选，从新到旧键集分页。
/// 审计记录只读，界面与 API 都不能修改或删除；CSV 导出在同分组的受限临时资源中提供。
/// </summary>
internal static class AuditEndpoints
{
    public const int DefaultPageSize = 50;

    public const int MaximumPageSize = 200;

    public static RouteGroupBuilder MapAuditEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/audit", List)
            .RequireAuthorization(AuthorizationPolicies.Administrator)
            .WithTags("Audit")
            .WithName("listAuditRecords")
            .WithSummary("查询审计记录")
            .WithDescription(
                "仅管理员。按发生时间（from 含、to 不含）、账号、操作代码、对象精确筛选，从新到旧排列；" +
                "下一页以上一页返回的 nextCursor 作为 before 参数。")
            .ProducesProblem(StatusCodes.Status403Forbidden);
        return api;
    }

    private static Results<Ok<AuditPage>, ValidationProblem> List(
        ConfigDatabase database,
        DateTimeOffset? from,
        DateTimeOffset? to,
        string? account,
        string? action,
        string? target,
        long? before,
        int? limit,
        HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        var pageSize = limit ?? DefaultPageSize;
        if (pageSize is < 1 or > MaximumPageSize)
        {
            return ApiProblems.Validation(new Dictionary<string, string[]>
            {
                ["limit"] = [$"每页条数须在 1—{MaximumPageSize} 之间。"],
            });
        }

        var filter = new AuditFilter(from, to, account, action, target);
        try
        { filter.Validate(); }
        catch (ArgumentException)
        { return ApiProblems.Validation(new Dictionary<string, string[]> { ["filter"] = ["请核对时间范围及筛选字段长度。"] }); }
        if (before is < 1 or > 9007199254740991L)
        { return ApiProblems.Validation(new Dictionary<string, string[]> { ["before"] = ["请使用上一页返回的有效游标。"] }); }
        using var client = database.CreateIsolatedClient();
        var query = filter.Apply(client).WhereIF(before.HasValue, record => record.Id < before!.Value)
            .OrderBy(record => record.Id, OrderByType.Desc);

        var records = query.Take(pageSize + 1).ToList();
        var hasMore = records.Count > pageSize;
        var items = records.Take(pageSize).Select(AuditRecord.From).ToList();
        return TypedResults.Ok(new AuditPage(items, hasMore ? items[^1].Id : null));
    }
}

/// <summary>一页审计记录；nextCursor 为空表示没有更早的记录。</summary>
internal sealed record AuditPage(IReadOnlyList<AuditRecord> Items, long? NextCursor);

/// <summary>一条审计记录。ID 恒小于 2^53，按数字序列化不丢精度（总设计 §10.1）。</summary>
internal sealed record AuditRecord(
    long Id,
    DateTimeOffset OccurredAt,
    string? AccountName,
    string? TokenName,
    string? SourceIp,
    string Action,
    string? Target,
    string Result,
    string? Detail)
{
    public static AuditRecord From(AuditLogRecord record) => new(
        record.Id,
        DateTimeOffset.FromUnixTimeMilliseconds(record.OccurredAt),
        record.AccountName,
        record.TokenName,
        record.SourceIp,
        record.Action,
        record.Target,
        record.Result,
        record.Detail);
}
