using Microsoft.AspNetCore.Http.HttpResults;
using SqlSugar;
using TenonEdge.Host.Bootstrap;
using TenonEdge.Host.Storage;

namespace TenonEdge.Host.Features.Runtime;

/// <summary>
/// 运行事件查询（runtime 分组，总设计 §10.1）：只读状态查询，所有角色可查看（总设计 §3.3）。
/// 按时间、级别、类型、对象筛选（功能需求 §7.4），按首次发生从新到旧键集分页；合并的事件给出首次与最近一次发生的时间和次数。
/// 时间筛选按事件从首次到最近一次发生的区间与查询区间是否相交判断。
/// </summary>
internal static class RuntimeEventEndpoints
{
    public const int DefaultPageSize = 50;

    public const int MaximumPageSize = 200;

    public static RouteGroupBuilder MapRuntimeEventEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/runtime/events", List)
            .WithTags("Runtime")
            .WithName("listRuntimeEvents")
            .WithSummary("查询运行事件")
            .WithDescription(
                "所有角色可查看。按时间（from 含、to 不含；合并的事件只要有一次发生落在区间内即列出）、级别（info、warning、error，与返回值相同）、" +
                "类型代码、对象筛选，按首次发生从新到旧排列；同一对象的同类事件在 5 分钟内合并为一条并计数。" +
                "下一页以上一页返回的 nextCursor 作为 before 参数。");
        return api;
    }

    /// <param name="level">按返回值的写法（小写）筛选；框架对枚举查询参数按大小写敏感解析，因此以字符串接收。</param>
    private static Results<Ok<RuntimeEventPage>, ValidationProblem> List(
        ConfigDatabase database,
        DateTimeOffset? from,
        DateTimeOffset? to,
        string? level,
        string? type,
        string? target,
        long? before,
        int? limit)
    {
        var errors = new Dictionary<string, string[]>();
        var pageSize = limit ?? DefaultPageSize;
        if (pageSize is < 1 or > MaximumPageSize)
        {
            errors["limit"] = [$"每页条数须在 1—{MaximumPageSize} 之间。"];
        }

        if (!string.IsNullOrEmpty(level) && !RuntimeEventLevels.IsName(level))
        {
            errors["level"] = ["级别只能是 info、warning 或 error。"];
        }

        if (errors.Count > 0)
        {
            return ApiProblems.Validation(errors);
        }

        var levelName = string.IsNullOrEmpty(level) ? null : level;
        var records = database.Client.Queryable<RuntimeEventRecord>()
            .WhereIF(from.HasValue, record => record.LastOccurredAt >= from!.Value.ToUnixTimeMilliseconds())
            .WhereIF(to.HasValue, record => record.FirstOccurredAt < to!.Value.ToUnixTimeMilliseconds())
            .WhereIF(levelName is not null, record => record.Level == levelName)
            .WhereIF(!string.IsNullOrEmpty(type), record => record.Type == type)
            .WhereIF(!string.IsNullOrEmpty(target), record => record.Target == target)
            .WhereIF(before.HasValue, record => record.Id < before!.Value)
            .OrderBy(record => record.Id, OrderByType.Desc)
            .Take(pageSize + 1)
            .ToList();

        var hasMore = records.Count > pageSize;
        var items = records.Take(pageSize).Select(RuntimeEventItem.From).ToList();
        return TypedResults.Ok(new RuntimeEventPage(items, hasMore ? items[^1].Id : null));
    }
}

/// <summary>一页运行事件；nextCursor 为空表示没有更早的事件。</summary>
internal sealed record RuntimeEventPage(IReadOnlyList<RuntimeEventItem> Items, long? NextCursor);

/// <summary>一条运行事件。对象为空表示事件不针对具体对象（例如服务启停）。</summary>
internal sealed record RuntimeEventItem(
    long Id,
    DateTimeOffset FirstOccurredAt,
    DateTimeOffset LastOccurredAt,
    RuntimeEventLevel Level,
    string Type,
    string? Target,
    string Message,
    int OccurrenceCount)
{
    public static RuntimeEventItem From(RuntimeEventRecord record) => new(
        record.Id,
        DateTimeOffset.FromUnixTimeMilliseconds(record.FirstOccurredAt),
        DateTimeOffset.FromUnixTimeMilliseconds(record.LastOccurredAt),
        RuntimeEventLevels.FromName(record.Level),
        record.Type,
        record.Target.Length == 0 ? null : record.Target,
        record.Message,
        record.OccurrenceCount);
}
