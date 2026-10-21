using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Data.Sqlite;
using TenonEdge.Host.Bootstrap;
using TenonEdge.Host.Features.Configuration;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Storage;

namespace TenonEdge.Host.Features.Routes;

internal sealed record RouteRuntimePage(string DraftVersion, long? Revision, bool Applied, DateTimeOffset CapturedAt, RouteNodeRuntimeInfo[] Nodes);

internal static class RouteRuntimeEndpoints
{
    internal static Results<Ok<RouteRuntimePage>, ProblemHttpResult> Read(long routeId, string expectedVersion,
        RouteDraftStore drafts, RuntimeCoordinator coordinator, RuntimeVersionStore versions, ConfigDatabase database,
        TimeProvider time, HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (routeId <= 0 || string.IsNullOrWhiteSpace(expectedVersion) || expectedVersion.Length > 64)
        { return ApiProblems.Problem(400, "route.invalidRuntimeQuery", "请提供路线标识与草稿版本。"); }
        try
        {
            var draft = drafts.Read(routeId);
            if (draft.Version != expectedVersion)
            { return Changed(); }
            var runtime = coordinator.Runtime;
            var status = runtime.GetStatus();
            var state = coordinator.Status;
            if (status.ActiveRevision != state.ActiveRevision || status.ActiveRevision != versions.ReadPointers().EffectiveRevision)
            { return Changed(); }
            RouteNodeRuntimeInfo[] nodes = [];
            var applied = false;
            if (status.ActiveRevision is { } revision)
            {
                var snapshot = versions.Load(revision) ?? throw new RuntimePlanDocumentException("有效运行快照缺失。");
                // Only frozen identities are needed here, not queue-only units or
                // diagnostic labels for every other route in the checkpoint.
                var (plan, checkpoint) = RuntimeSnapshotDocument.Read(snapshot);
                if (checkpoint is null)
                { throw new RuntimePlanDocumentException("旧种子计划没有路线图，不能猜测节点归属。"); }
                var route = checkpoint.Routes.SingleOrDefault(route => route.Id == routeId);
                if (route is not null)
                {
                    applied = true;
                    nodes = RouteNodeRuntime.Read(plan, checkpoint, route, status,
                        id => runtime.Messages.Read(id, includeBody: false), context.RequestAborted);
                }
            }
            if (runtime.ActiveRevision != status.ActiveRevision || coordinator.Status != state ||
                versions.ReadPointers().EffectiveRevision != status.ActiveRevision ||
                database.InTransaction(ConfigurationDraft.ReadVersion) != expectedVersion)
            { return Changed(); }
            return TypedResults.Ok(new RouteRuntimePage(expectedVersion, status.ActiveRevision, applied, time.GetUtcNow(), nodes));
        }
        catch (DraftWriteException error)
        { return ApiProblems.Problem(error.Code == "route.notFound" ? 404 : 409, error.Code, "路线或草稿版本已变化，请重新读取。"); }
        catch (Exception error) when (error is RuntimePlanDocumentException or ArgumentException or System.Text.Json.JsonException or InvalidOperationException)
        { return ApiProblems.Problem(409, "route.runtimeUnavailable", "有效快照或节点归属暂不可核对；不以草稿推断运行情况。"); }
        catch (Exception error) when (error is SqliteException or IOException or UnauthorizedAccessException)
        { return ApiProblems.Problem(503, "route.runtimeStorageUnavailable", "配置存储暂不可读；未使用旧缓存代替本次核对。"); }
    }
    private static ProblemHttpResult Changed() => ApiProblems.Problem(409, "route.runtimeChanged", "运行或草稿版本正在变化，请重新核对；未应用配置。");
}
