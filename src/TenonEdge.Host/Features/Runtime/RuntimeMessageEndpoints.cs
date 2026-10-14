using System.Globalization;
using Microsoft.AspNetCore.Http.HttpResults;
using TenonEdge.Abstractions.Plans;
using TenonEdge.Host.Bootstrap;
using TenonEdge.Host.Security;
using TenonEdge.Runtime.Diagnostics;
using TenonEdge.Runtime.Lifecycle;

namespace TenonEdge.Host.Features.Runtime;

internal sealed record RuntimeMessageObject(string Kind, string Key, bool Unresolved);
internal sealed record RuntimeMessageObjectPage(string? Revision, RuntimeMessageObject[] Items, string? NextCursor);
// Deliberately not RuntimeMessageRecord: metadata responses have no body property at all.
internal sealed record RuntimeMessageInfo(string Sequence, string Revision, Guid SourceMessageId, DateTimeOffset CapturedAt,
    int OriginalBytes, int StoredBytes, bool Truncated, bool PrivacyBlocked, RuntimeMessageDetails? Details);
internal sealed record RuntimeMessagePage(string Revision, RuntimeMessageObject Object, RuntimeMessageInfo[] Items);
internal sealed record RuntimeMessageBody(RuntimeMessageInfo Record, string Body)
{
    public override string ToString() => $"Runtime message body {Record.Sequence} (omitted)";
}

internal static class RuntimeMessageEndpoints
{
    public static RouteGroupBuilder MapRuntimeMessageEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/runtime/messages/objects", Objects).WithTags("Runtime").WithName("getRuntimeMessageObjects")
            .WithSummary("分页查看当前运行输入及逻辑输出节点；不触发采集或发送");
        api.MapGet("/runtime/messages", Messages).WithTags("Runtime").WithName("getRuntimeMessages")
            .WithSummary("查看单对象至多 100 条诊断元数据，所有角色均不在此接口取得正文");
        api.MapGet("/runtime/messages/{sequence}/body", Body).WithTags("Runtime").WithName("getRuntimeMessageBody")
            .WithSummary("显式读取所选诊断记录的至多 64 KiB 正文前缀；不重放，不替换已淘汰记录")
            .RequireAuthorization(AuthorizationPolicies.Configurator).ProducesProblem(403);
        return api;
    }

    private static Results<Ok<RuntimeMessageObjectPage>, ProblemHttpResult> Objects(string? after, string? revision, int? limit,
        RuntimeHost runtime, HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (limit is < 1 or > 100 || revision is not null && !Number(revision, out _) ||
            after is not null && (revision is null || !Cursor(after)))
        { return Invalid(); }
        var catalog = runtime.GetMessageCatalog();
        if (revision is not null && revision != Text(catalog.Revision ?? 0))
        { return Changed(); }
        var page = catalog.Objects.Where(id => after is null || string.CompareOrdinal(id.ToString(), after) > 0).Take((limit ?? 100) + 1).ToArray();
        var items = page.Take(limit ?? 100).ToArray();
        if (runtime.ActiveRevision != catalog.Revision)
        { return Changed(); }
        return TypedResults.Ok(new RuntimeMessageObjectPage(catalog.Revision is { } active ? Text(active) : null,
            items.Select(Describe).ToArray(), page.Length > items.Length ? items[^1].ToString() : null));
    }

    private static Results<Ok<RuntimeMessagePage>, ProblemHttpResult> Messages(string kind, string key, string revision,
        RuntimeHost runtime, HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        var error = Select(runtime, kind, key, revision, out var id);
        if (error is not null)
        { return error; }
        var records = runtime.Messages.Read(id!, includeBody: false).Select(Describe).ToArray();
        if (Text(runtime.ActiveRevision ?? 0) != revision)
        { return Changed(); }
        return TypedResults.Ok(new RuntimeMessagePage(revision, Describe(id!), records));
    }

    private static Results<Ok<RuntimeMessageBody>, ProblemHttpResult> Body(string sequence, string kind, string key, string revision,
        string recordRevision, Guid sourceMessageId, RuntimeHost runtime, HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!Number(sequence, out var selected) || selected <= 0 || !Number(recordRevision, out var capturedRevision) ||
            capturedRevision <= 0 || sourceMessageId == Guid.Empty)
        { return Invalid(); }
        var error = Select(runtime, kind, key, revision, out var id);
        if (error is not null)
        { return error; }
        // Match all immutable identities inside the cache lock, and decode only this record.
        var record = runtime.Messages.ReadRecord(id!, selected, capturedRevision, sourceMessageId);
        if (Text(runtime.ActiveRevision ?? 0) != revision)
        { return Changed(); }
        if (record is null)
        { return ApiProblems.Problem(404, "runtime.messageUnavailable", "所选记录已淘汰、重启清空或身份不符；未替换记录。"); }
        if (record.PrivacyBlocked)
        { return ApiProblems.Problem(403, "runtime.messagePrivate", "该记录未保存可供查看的正文。"); }
        return TypedResults.Ok(new RuntimeMessageBody(Describe(record), record.Body!));
    }

    private static ProblemHttpResult? Select(RuntimeHost runtime, string kind, string key, string revision, out RuntimeUnitId? id)
    {
        id = null;
        if (!Number(revision, out var expected) || expected <= 0)
        { return Invalid(); }
        try
        { id = RuntimeUnitId.Create(kind, key); }
        catch (ArgumentException) { return Invalid(); }
        var catalog = runtime.GetMessageCatalog();
        if (expected != catalog.Revision)
        { return Changed(); }
        return catalog.Objects.Contains(id) ? null : ApiProblems.Problem(404, "runtime.messageObjectNotFound", "当前运行计划没有该诊断对象。");
    }

    private static bool Cursor(string value)
    {
        var split = value.IndexOf('/');
        if (value.Length > 324 || split <= 0)
        { return false; }
        try
        { _ = RuntimeUnitId.Create(value[..split], value[(split + 1)..]); return true; }
        catch (ArgumentException) { return false; }
    }
    private static bool Number(string value, out long number) => long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out number) && Text(number) == value;
    private static string Text(long number) => number.ToString(CultureInfo.InvariantCulture);
    private static RuntimeMessageObject Describe(RuntimeUnitId id) => new(id.Kind, id.Key, id.Kind.StartsWith("output.", StringComparison.Ordinal) && id.Key == "unresolved");
    private static RuntimeMessageInfo Describe(RuntimeMessageRecord record) => new(Text(record.Sequence), Text(record.Revision), record.SourceMessageId,
        record.CapturedAt, record.OriginalBytes, record.StoredBytes, record.Truncated, record.PrivacyBlocked, record.Details);
    private static ProblemHttpResult Invalid() => ApiProblems.Problem(400, "runtime.invalidMessageQuery", "诊断查询参数无效。");
    private static ProblemHttpResult Changed() => ApiProblems.Problem(409, "runtime.revisionChanged", "运行版本已切换，请重新选择诊断对象。");
}
