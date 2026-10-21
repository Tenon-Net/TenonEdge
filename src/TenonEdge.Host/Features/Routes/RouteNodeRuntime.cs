using TenonEdge.Abstractions.Plans;
using TenonEdge.Host.Features.Configuration;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Runtime.Diagnostics;
using TenonEdge.Runtime.Lifecycle;

namespace TenonEdge.Host.Features.Routes;

internal sealed record RouteNodeRuntimeInfo(string Id, string Kind, int Version, string State,
    int Inputs, int RunningInputs, int FaultedInputs, int Paths, int ObservedPaths, int FailedPaths, int UnknownPaths,
    DateTimeOffset? LastObservedAt, string? LastOutcome);

/// <summary>只聚合有效快照的拓扑和既有有界元数据，不执行模块、解密凭证或复制正文。
/// 每个来源/路径各取最新一条；其他分支的成功不能掩盖该路径失败。入队不是发送确认。</summary>
internal static class RouteNodeRuntime
{
    internal static RouteNodeRuntimeInfo[] Read(RuntimePlanDocument plan, ConfigurationCheckpoint checkpoint, RouteDraft route,
        RuntimeStatusReport status, Func<RuntimeUnitId, IReadOnlyList<RuntimeMessageRecord>> records, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var nodes = route.Graph.Nodes.ToDictionary(node => node.Id, node => new Node(node));
        if (!route.Enabled)
        { return nodes.Values.Select(node => node.Result("disabled", 0, 0)).ToArray(); }
        var (sources, _, topology) = RoutePlanCompiler.RecoverDiagnosticTopology(plan, checkpoint with { Routes = [route] });
        var units = status.Units.GroupBy(unit => unit.Id).ToDictionary(group => group.Key, group => group.Last());
        var known = plan.Units.Select(unit => unit.Id).ToHashSet();
        var observations = new Dictionary<(string Node, string Path, string Input), RuntimeMessageRecord>();
        var latestInputs = new Dictionary<RuntimeUnitId, RuntimeMessageRecord?>();
        foreach (var node in route.Graph.Nodes.Where(node => node.Type.StartsWith("output.", StringComparison.Ordinal)))
        {
            token.ThrowIfCancellationRequested();
            foreach (var record in records(RuntimeUnitId.Create(node.Type, route.Key + "/" + node.Id)))
            {
                if (record.Revision != status.ActiveRevision || record.Details is not { PathKey: { } path } details)
                { continue; }
                var key = (node.Id, path, details.InputKey);
                if (!observations.TryGetValue(key, out var old) || old.Sequence < record.Sequence)
                { observations[key] = record; }
            }
        }
        foreach (var path in topology.Paths)
        {
            token.ThrowIfCancellationRequested();
            var output = nodes[path.Nodes[^1]].Definition;
            var id = RoutePlanCompiler.OutputPathId(output.Type, route.Key, path.Nodes);
            if (!known.Contains(id))
            { throw new RuntimePlanDocumentException("有效路线的路径与冻结计划不匹配。"); }
            var source = sources[path.SourceId].UnitId;
            observations.TryGetValue((output.Id, id.Key, source.Key), out var record);
            for (var index = 0; index < path.Nodes.Length; index++)
            {
                var node = nodes[path.Nodes[index]];
                node.Sources.Add(source);
                node.Paths++;
                if (index == 0 || record is null)
                { continue; }
                var outcome = index == path.Nodes.Length - 1 ? OutputOutcome(record.Details!.Outcome) : TransformOutcome(record.Details!, index - 1, path.Nodes.Length - 2);
                node.Observe(record.Sequence, record.CapturedAt, outcome);
            }
        }
        var result = new List<RouteNodeRuntimeInfo>(nodes.Count);
        foreach (var node in nodes.Values)
        {
            token.ThrowIfCancellationRequested();
            var running = 0;
            var faulted = 0;
            var input = node.Definition.Type.StartsWith("input.", StringComparison.Ordinal);
            foreach (var source in node.Sources)
            {
                var unit = units.GetValueOrDefault(source);
                if (unit?.State == RuntimeUnitState.Running)
                { running++; }
                if (unit?.State is RuntimeUnitState.Backoff or RuntimeUnitState.BuildFailed or RuntimeUnitState.StopTimedOut ||
                    unit?.Reads?.Health is InputReadHealth.Failing or InputReadHealth.Offline or InputReadHealth.PartialFailure)
                { faulted++; }
                if (input)
                {
                    // A source may be referenced by several canvas nodes. Read its
                    // bounded metadata once per request, not once per reference.
                    if (!latestInputs.TryGetValue(source, out var latest))
                    {
                        latest = records(source).Where(record => record.Revision == status.ActiveRevision)
                            .MaxBy(record => record.Sequence);
                        latestInputs.Add(source, latest);
                    }
                    if (latest is not null)
                    { node.Latest(latest.Sequence, latest.CapturedAt, "received"); }
                }
            }
            var state = faulted > 0 ? "degraded" : running < node.Sources.Count ? "sourceUnavailable" :
                input ? "running" : node.Failed > 0 ? "pathFailure" : node.Unknown > 0 ? "unknown" :
                node.Observed == 0 ? "waiting" : node.Observed < node.Paths ? "partialObservation" : "observed";
            result.Add(node.Result(state, running, faulted));
        }
        return result.ToArray();
    }

    private static string TransformOutcome(RuntimeMessageDetails details, int stage, int stages)
    {
        if (details.Outcome == RuntimeMessageOutcome.MappingRejected)
        {
            return details.FailedMappingStage is not { } failed || failed < 0 || failed >= stages ? "unknown" :
                stage < failed ? "processed" : stage == failed ? "mappingRejected" : "upstreamRejected";
        }
        // FormattingRejected can also be an ArgumentException in path processing;
        // no stage identity was captured, so don't invent a successful transform.
        return details.Outcome is RuntimeMessageOutcome.Generated or RuntimeMessageOutcome.Queued or
            RuntimeMessageOutcome.QuotaRejected or RuntimeMessageOutcome.StorageProtected or RuntimeMessageOutcome.DataRejected or
            RuntimeMessageOutcome.StorageFailed or RuntimeMessageOutcome.Stopped or RuntimeMessageOutcome.QueueOutcomeUnknown
            ? "processed" : "unknown";
    }
    private static string OutputOutcome(RuntimeMessageOutcome outcome) => outcome switch
    {
        RuntimeMessageOutcome.MappingRejected => "upstreamRejected",
        RuntimeMessageOutcome.Generated => "generated",
        RuntimeMessageOutcome.Queued => "queued",
        RuntimeMessageOutcome.FormattingRejected => "formattingRejected",
        RuntimeMessageOutcome.QuotaRejected => "quotaRejected",
        RuntimeMessageOutcome.StorageProtected => "storageProtected",
        RuntimeMessageOutcome.DataRejected => "dataRejected",
        RuntimeMessageOutcome.StorageFailed => "storageFailed",
        RuntimeMessageOutcome.Stopped => "stopped",
        RuntimeMessageOutcome.QueueOutcomeUnknown => "queueOutcomeUnknown",
        _ => "unknown",
    };

    private sealed class Node(RouteGraphNode definition)
    {
        internal RouteGraphNode Definition { get; } = definition;
        internal HashSet<RuntimeUnitId> Sources { get; } = [];
        internal int Paths { get; set; }
        internal int Observed { get; private set; }
        internal int Failed { get; private set; }
        internal int Unknown { get; private set; }
        private DateTimeOffset? _at;
        private long _sequence;
        private string? _outcome;
        internal void Observe(long sequence, DateTimeOffset at, string outcome)
        {
            Observed++;
            if (outcome is "unknown" or "queueOutcomeUnknown")
            { Unknown++; }
            else if (outcome is not ("processed" or "queued" or "generated"))
            { Failed++; }
            Latest(sequence, at, outcome);
        }
        internal void Latest(long sequence, DateTimeOffset at, string outcome)
        {
            // Wall-clock corrections must not reorder actual observations.
            if (_at is null || sequence > _sequence)
            { _at = at; _sequence = sequence; _outcome = outcome; }
        }
        internal RouteNodeRuntimeInfo Result(string state, int running, int faulted) => new(Definition.Id, Definition.Type, Definition.Version,
            state, Sources.Count, running, faulted, Paths, Observed, Failed, Unknown, _at, _outcome);
    }
}
