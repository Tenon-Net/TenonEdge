using Microsoft.AspNetCore.Http.HttpResults;
using TenonEdge.Host.Bootstrap;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Runtime.Diagnostics;

namespace TenonEdge.Host.Features.Configuration;

internal static class ConfigurationVersionEndpoints
{
    public static RouteGroupBuilder MapConfigurationVersionEndpoints(this RouteGroupBuilder api)
    {
        // 查看配置向全部已登录角色开放；无应用、导出或真实采集副作用。
        var group = api.MapGroup("/configuration/versions").WithTags("Configuration");
        group.MapGet("/", List).WithName("listConfigurationVersions")
            .WithSummary("按版本号降序查看持久化历史元数据；下一页使用本页最后的 revision 作为 beforeRevision");
        group.MapGet("/{revision:long}", Get).WithName("getConfigurationVersion")
            .WithSummary("查看校验哈希后的非秘密版本内容；旧版本缺失的完整草稿不补造")
            .ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(429);
        group.MapGet("/{revision:long}/compare/{otherRevision:long}", Compare).WithName("compareConfigurationVersions")
            .WithSummary("只读比较两个保留版本；revision 为旧侧，otherRevision 为新侧，不校验是否可回滚")
            .ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(429);
        group.MapGet("/{revision:long}/compare-draft", CompareDraft).WithName("compareConfigurationVersionToDraft")
            .WithSummary("只读比较历史版本与指定版本号的当前全局草稿；草稿已变化时返回冲突")
            .ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(429);
        return api;
    }

    private static Results<Ok<RuntimeVersionSummary[]>, ValidationProblem> List(RuntimeVersionStore versions, HttpContext context,
        long? beforeRevision = null, int limit = 20)
    {
        context.Response.Headers.CacheControl = "no-store";
        try
        { return TypedResults.Ok(versions.ReadHistory(beforeRevision, limit)); }
        catch (ArgumentException) { return InvalidParameters(); }
    }

    private static Results<Ok<ConfigurationVersionDetail>, ValidationProblem, ProblemHttpResult> Get(long revision,
        RuntimeVersionStore versions, PreviewOperationBudget budget, HttpContext context) =>
        Read(() => versions.ReadVersion(revision, context.RequestAborted), budget, context);

    private static Results<Ok<ConfigurationVersionComparison>, ValidationProblem, ProblemHttpResult> Compare(long revision, long otherRevision,
        RuntimeVersionStore versions, PreviewOperationBudget budget, HttpContext context) =>
        Read(() => versions.CompareVersionContents(revision, otherRevision, context.RequestAborted), budget, context);

    private static Results<Ok<ConfigurationVersionComparison>, ValidationProblem, ProblemHttpResult> CompareDraft(long revision, string expectedVersion,
        RuntimeVersionStore versions, PreviewOperationBudget budget, HttpContext context) =>
        Read(() => versions.CompareVersionToDraft(revision, expectedVersion, context.RequestAborted), budget, context);

    private static Results<Ok<T>, ValidationProblem, ProblemHttpResult> Read<T>(Func<T> read, PreviewOperationBudget budget, HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        using var admission = ConnectionTestAdmission.TryAcquire(context.User, budget, out var failure);
        if (failure is not null)
        { return failure; }
        try
        { return TypedResults.Ok(read()); }
        catch (DraftWriteException error) { return ApiProblems.Problem(409, error.Code, "草稿已变化，请刷新后比较。"); }
        catch (KeyNotFoundException) { return ApiProblems.Problem(404, "configuration.versionNotFound", "版本不存在或已清理。"); }
        catch (NotSupportedException) { return ApiProblems.Problem(409, "configuration.unsupportedSnapshot", "旧版快照没有完整草稿，不能按配置对象比较。"); }
        catch (RuntimePlanDocumentException) { return ApiProblems.Problem(409, "configuration.invalidSnapshot", "配置快照无效或超过大小限制，不能读取或比较。"); }
        catch (ArgumentException) { return InvalidParameters(); }
    }

    private static ValidationProblem InvalidParameters() => ApiProblems.Validation(new Dictionary<string, string[]>
    { ["version"] = ["版本号和分页游标须为正整数，页大小须为 1—100，比较草稿须提供有效的 expectedVersion。"] });
}
