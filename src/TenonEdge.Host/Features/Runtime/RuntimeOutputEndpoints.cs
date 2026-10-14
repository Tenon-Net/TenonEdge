using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Data.Sqlite;
using TenonEdge.Abstractions.Plans;
using TenonEdge.Host.Bootstrap;
using TenonEdge.Host.Features.Deliveries;
using TenonEdge.Runtime.Deliveries;
using TenonEdge.Runtime.Diagnostics;
using TenonEdge.Runtime.Lifecycle;
using TenonEdge.Runtime.Outputs;

namespace TenonEdge.Host.Features.Runtime;

internal sealed record OutputQueueLimits(int TargetCount, long TargetBytes, long GlobalBytes, double PendingRetentionSeconds,
    int FailedTargetCount, int FailedGlobalCount, double FailedRetentionSeconds, long DiskReserveBytes, double DiskReserveFraction);
internal sealed record RuntimeOutputInfo(long ConnectionId, string? Key, string? Name, string? Kind, bool Active,
    string State, RuntimeUnitState? UnitState, OutputSenderDiagnostics? Sender, DeliveryTargetQueueReport Queue,
    double? HeadWaitingSeconds, double? HeadBlockedSeconds, OutputTrafficSnapshot Traffic, OutputCapacityEstimate Capacity);
internal sealed record RuntimeOutputPage(long? Revision, bool MetadataAvailable, DateTimeOffset CapturedAt,
    DeliveryQueueUsage Usage, DeliveryQueuePolicyCounters Counters, DeliveryQueueFiles? Files, OutputQueueLimits Limits,
    StorageProtectionStatus StorageProtection, IReadOnlyDictionary<string, int> ActiveStates, RuntimeOutputInfo[] Items, long? NextConnectionId);

/// <summary>只读诊断不触发发送。运行名称取有效运行快照，孤立记录仍列出但不猜测原目标。
/// 队列为短事务快照，运行/速率为内存快照；二者不是跨库原子采样。端点不返回报文、地址、头或凭证。</summary>
internal static class RuntimeOutputEndpoints
{
    public static RouteGroupBuilder MapRuntimeOutputEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/runtime/outputs", List).WithTags("Runtime").WithName("getRuntimeOutputs")
            .WithSummary("分页查看输出状态、真实队列占用及容量估算边界");
        api.MapGet("/runtime/outputs/{connectionId:long}", Detail).WithTags("Runtime").WithName("getRuntimeOutput")
            .WithSummary("查看输出队头、失败隔离统计及最近一小时趋势，不返回报文正文");
        return api;
    }

    private static Results<Ok<RuntimeOutputPage>, ProblemHttpResult> List(long? after, int? limit, long? revision,
        RuntimeCoordinator coordinator, RuntimeVersionStore versions, OutputControlStore controls, DeliveryCache cache, HttpContext context)
        => Read(null, after ?? 0, limit ?? 100, revision, coordinator, versions, controls, cache, context);

    private static Results<Ok<RuntimeOutputPage>, ProblemHttpResult> Detail(long connectionId, long? revision,
        RuntimeCoordinator coordinator, RuntimeVersionStore versions, OutputControlStore controls, DeliveryCache cache, HttpContext context)
        => Read(connectionId, connectionId - 1, 1, revision, coordinator, versions, controls, cache, context);

    private static Results<Ok<RuntimeOutputPage>, ProblemHttpResult> Read(long? selected, long after, int limit, long? revision,
        RuntimeCoordinator coordinator, RuntimeVersionStore versions, OutputControlStore controls, DeliveryCache cache, HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (after is < 0 or > 9007199254740991L || selected is < 1 or > 9007199254740991L || limit is < 1 or > 100 || revision < 0)
        { return ApiProblems.Problem(400, "runtime.invalidOutputQuery", "输出查询参数无效。"); }
        try
        {
            var runtime = coordinator.Runtime;
            var status = runtime.GetStatus();
            if (revision is not null && revision != (status.ActiveRevision ?? 0))
            { return Changed(); }
            var catalog = Catalog(versions, status.ActiveRevision);
            var report = cache.GetQueueReport(catalog.Keys.ToArray(), after, limit);
            if (selected is not null && (report.Targets.Length == 0 || report.Targets[0].ConnectionId != selected))
            { return ApiProblems.Problem(404, "runtime.outputNotFound", "当前运行计划与队列中不存在该目标。"); }
            var senders = runtime.GetOutputSenders().ToDictionary(item => item.ConnectionId, item => item.Sender);
            var manual = controls.ReadMany(catalog.Keys.Concat(report.Targets.Select(item => item.ConnectionId)).Distinct().ToArray());
            var states = new Dictionary<long, (string State, RuntimeUnitState? Unit, OutputSenderDiagnostics? Sender)>();
            foreach (var (id, target) in catalog)
            {
                var sender = senders.GetValueOrDefault(id);
                // 同连接兼容变更会保留旧路径ID的发送者；按真实实例标识查监督状态，不能按新图ID误报未运行。
                var unit = sender?.UnitId is { } senderId ? status.Units.LastOrDefault(unit => unit.Id == senderId)
                    : status.Units.LastOrDefault(unit => target.Units.Contains(unit.Id));
                var state = manual.GetValueOrDefault(id)?.ManuallyPaused == true ? "manuallyPaused"
                    : unit?.State != RuntimeUnitState.Running ? "notRunning" : sender?.State ?? "notRunning";
                states.Add(id, (state, unit?.State, sender));
            }
            var traffic = catalog.Keys.ToDictionary(id => id, id => cache.OutputTraffic.Read(id, includePoints: id == selected));
            var storage = cache.StorageProtection.Status;
            var options = cache.CurrentPolicy;
            var items = report.Targets.Select(queue =>
            {
                var id = queue.ConnectionId;
                var target = catalog.GetValueOrDefault(id);
                var (state, unit, sender) = states.GetValueOrDefault(id, ("inactive", null, null));
                if (manual.GetValueOrDefault(id)?.ManuallyPaused == true)
                { state = "manuallyPaused"; }
                var rates = traffic.GetValueOrDefault(id) ?? new OutputTrafficSnapshot(id, [], null, null, null, null);
                // 场景：此目标现在断网，加上其他生成多于成功发送、退避/暂停/未运行或队头阻塞的目标；只对当前计划生成率估算。
                var accumulating = traffic.Where(pair => pair.Key == id || pair.Value.Accumulating == true || states[pair.Key].State != "normal" ||
                    states[pair.Key].Sender?.HeadBlockedSince is not null).Select(pair => pair.Value.BytesPerSecond).ToArray();
                double? totalRate = target is not null && accumulating.All(rate => rate is not null) ? accumulating.Sum(rate => rate!.Value) : null;
                var blockedSince = sender?.HeadSequence == queue.HeadSequence && queue.HeadSequence is not null ? sender?.HeadBlockedSince : null;
                return new RuntimeOutputInfo(id, target?.Key, target?.Name, target?.Kind, target is not null, state, unit, sender,
                    queue, queue.HeadClockAnomaly ? null : Age(queue.HeadCreatedAt, report.CapturedAt), Age(blockedSince, report.CapturedAt), rates,
                    OutputCapacityEstimator.Estimate(options, queue.Usage, report.Usage, rates.RecordsPerSecond,
                        rates.BytesPerSecond, totalRate, storage));
            }).ToArray();
            if (runtime.ActiveRevision != status.ActiveRevision)
            { return Changed(); }
            return TypedResults.Ok(new RuntimeOutputPage(status.ActiveRevision,
                status.ActiveRevision is not null || versions.ReadPointers().EffectiveRevision is null, report.CapturedAt,
                report.Usage, report.Counters, report.Files,
                new(options.MaximumPendingPerTarget, options.MaximumPendingBytesPerTarget, options.MaximumQueueBytes,
                    options.PendingRetention.TotalSeconds, options.MaximumFailedPerTarget, options.MaximumFailedGlobal,
                    options.FailedRetention.TotalSeconds, options.MinimumDiskFreeBytes, options.MinimumDiskFreeFraction),
                storage, states.Values.GroupBy(item => item.State).ToDictionary(group => group.Key, group => group.Count()),
                items, selected is null ? report.NextConnectionId : null));
        }
        catch (RuntimePlanDocumentException)
        { return ApiProblems.Problem(409, "runtime.snapshotUnavailable", "运行快照不可读取，不能用草稿替代。"); }
        catch (Exception exception) when (exception is SqliteException or IOException or UnauthorizedAccessException)
        { return ApiProblems.Problem(503, "runtime.queueUnavailable", "队列统计暂不可读取，不能当作零记录。"); }
    }

    private static double? Age(DateTimeOffset? since, DateTimeOffset now) => since is { } value && value <= now ? (now - value).TotalSeconds : null;
    private static ProblemHttpResult Changed() => ApiProblems.Problem(409, "runtime.revisionChanged", "运行版本已切换，请刷新输出列表。");

    private static Dictionary<long, Target> Catalog(RuntimeVersionStore versions, long? revision)
    {
        if (revision is null)
        { return []; }
        var snapshot = versions.Load(revision.Value) ?? throw new RuntimePlanDocumentException("运行快照不存在。");
        var (plan, draft) = RuntimeSnapshotDocument.ReadForExecution(snapshot);
        return plan.Units.Where(unit => unit.Id.Kind.StartsWith("output.", StringComparison.Ordinal))
            .GroupBy(unit => unit.Configuration.GetProperty("connectionId").GetInt64()).ToDictionary(group => group.Key, group =>
            {
                var connection = draft?.Connections.FirstOrDefault(item => item.Id == group.Key);
                return new Target(connection?.Key, connection?.Name, group.First().Id.Kind[7..], group.Select(unit => unit.Id).ToArray());
            });
    }
    private sealed record Target(string? Key, string? Name, string Kind, RuntimeUnitId[] Units);
}
