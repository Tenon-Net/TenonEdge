using System.Text.Json;
using TenonEdge.Abstractions.Plans;
using TenonEdge.Host.Features.Configuration;
using TenonEdge.Host.Features.Routes;
using TenonEdge.Runtime.Diagnostics;
using TenonEdge.Runtime.Lifecycle;

namespace TenonEdge.UnitTests.Routes;

public sealed partial class RoutePlanCompilerTests
{
    private static readonly DateTimeOffset ObservationTime = DateTimeOffset.Parse("2026-10-19T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
    private static ConfigurationCheckpoint Checkpoint(RouteCompilationSnapshot snapshot) => new(snapshot.Version,
        snapshot.Connections.Values.ToArray(), snapshot.Devices.Values.ToArray(), snapshot.Inputs.Values.ToArray(), snapshot.Routes, []);
    private static RuntimeStatusReport NodeStatus(RoutePlanCompilation compiled) => new(7,
        compiled.Document.Units.Where(unit => unit.Id.Kind.StartsWith("input.", StringComparison.Ordinal))
            .Select(unit => new RuntimeUnitStatus(unit.Id, RuntimeUnitState.Running, 0, null, null, null, null)).ToArray());
    private static RuntimeMessageRecord NodeRecord(RouteCompiledPath path, long sequence, RuntimeMessageOutcome outcome, int? failedStage = null) => new(sequence,
        RuntimeUnitId.Create(path.OutputId.Kind, "route/" + path.Path.Nodes[^1]), 7, Guid.NewGuid(), ObservationTime, 100, 10, false,
        "private body never returned", Details: new($"d{path.Path.SourceId}/values", Guid.NewGuid(), outcome, FailedMappingStage: failedStage, PathKey: path.OutputId.Key));
    private static RouteNodeRuntimeInfo[] NodeView(RouteCompilationSnapshot snapshot, RoutePlanCompilation compiled,
        RuntimeMessageRecord[] records, RuntimeStatusReport? status = null) => RouteNodeRuntime.Read(compiled.Document,
            Checkpoint(snapshot), snapshot.Routes[0], status ?? NodeStatus(compiled), id => records.Where(record => record.ObjectId == id).ToArray(), Token);

    [Fact]
    public void NodeRuntimeKeepsEachSourcePathFailureVisibleAcrossFanOutAndFanIn()
    {
        var snapshot = Snapshot(Route([Input(sources: "[1,2]"), Map("a", "value", "value"), Map("b", "value", "value"), Output()],
            [new("in", "a"), new("in", "b"), new("a", "out"), new("b", "out")]), 2);
        var compiled = Compile(snapshot);
        var records = compiled.Paths.Select((path, index) => NodeRecord(path, index + 1,
            path.Path.SourceId == 1 && path.Path.Nodes.Contains("a") ? RuntimeMessageOutcome.MappingRejected : RuntimeMessageOutcome.Queued, 0)).ToArray();
        var result = NodeView(snapshot, compiled, records).ToDictionary(node => node.Id);
        Assert.Equal(2, result["in"].RunningInputs);
        Assert.Equal(2, result["a"].ObservedPaths);
        Assert.Equal(1, result["a"].FailedPaths);
        Assert.Equal("pathFailure", result["a"].State);
        Assert.Equal(0, result["b"].FailedPaths);
        Assert.Equal("observed", result["b"].State);
        Assert.Equal(4, result["out"].Paths);
        Assert.Equal(4, result["out"].ObservedPaths);
        Assert.Equal(1, result["out"].FailedPaths);
        Assert.Equal("pathFailure", result["out"].State);
        Assert.DoesNotContain("private", JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Fact]
    public void NodeRuntimeAttributesFailedStageAndDoesNotPretendDownstreamNodesRan()
    {
        var snapshot = Snapshot(Route([Input(), Map("a", "value", "value"), Map("b", "value", "value"), Map("c", "value", "value"), Output()],
            [new("in", "a"), new("a", "b"), new("b", "c"), new("c", "out")]));
        var compiled = Compile(snapshot);
        var failure = NodeRecord(Assert.Single(compiled.Paths), 1, RuntimeMessageOutcome.MappingRejected, 1);
        var failed = NodeView(snapshot, compiled, [failure]).ToDictionary(node => node.Id);
        Assert.Equal("processed", failed["a"].LastOutcome);
        Assert.Equal("mappingRejected", failed["b"].LastOutcome);
        Assert.Equal("upstreamRejected", failed["c"].LastOutcome);
        Assert.Equal("upstreamRejected", failed["out"].LastOutcome);
        var recovered = NodeView(snapshot, compiled, [failure, NodeRecord(compiled.Paths[0], 2, RuntimeMessageOutcome.Queued)]);
        Assert.All(recovered, node => Assert.Equal(0, node.FailedPaths));
        Assert.Equal("queued", recovered.Single(node => node.Id == "out").LastOutcome);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(99)]
    public void NodeRuntimeNeverGuessesAnInvalidFailureStage(int? failedStage)
    {
        var snapshot = Snapshot(Route([Input(), Map("map", "value", "value"), Output()], [new("in", "map"), new("map", "out")]));
        var compiled = Compile(snapshot);
        var result = NodeView(snapshot, compiled, [NodeRecord(compiled.Paths[0], 1, RuntimeMessageOutcome.MappingRejected, failedStage)]);
        Assert.Equal("unknown", result.Single(node => node.Id == "map").State);
        Assert.Equal(1, result.Single(node => node.Id == "map").UnknownPaths);
    }

    [Theory]
    [InlineData(RuntimeMessageOutcome.Generated, "generated")]
    [InlineData(RuntimeMessageOutcome.Queued, "queued")]
    [InlineData(RuntimeMessageOutcome.QuotaRejected, "quotaRejected")]
    [InlineData(RuntimeMessageOutcome.StorageProtected, "storageProtected")]
    [InlineData(RuntimeMessageOutcome.DataRejected, "dataRejected")]
    [InlineData(RuntimeMessageOutcome.StorageFailed, "storageFailed")]
    [InlineData(RuntimeMessageOutcome.Stopped, "stopped")]
    [InlineData(RuntimeMessageOutcome.QueueOutcomeUnknown, "queueOutcomeUnknown")]
    public void OutputNodeKeepsTheActualQueueOutcomeNotAnAssumedDeliveryAcknowledgement(RuntimeMessageOutcome outcome, string code)
    {
        var snapshot = Snapshot();
        var compiled = Compile(snapshot);
        var output = NodeView(snapshot, compiled, [NodeRecord(compiled.Paths[0], 1, outcome)]).Single(node => node.Id == "out");
        Assert.Equal(code, output.LastOutcome);
        Assert.Equal(1, output.ObservedPaths);
        Assert.DoesNotContain("ack", output.LastOutcome, StringComparison.Ordinal);
    }

    [Fact]
    public void OldRevisionsUnknownPathsAndUnrelatedSourcesAreNotCurrentNodeObservations()
    {
        var snapshot = Snapshot();
        var compiled = Compile(snapshot);
        var record = NodeRecord(compiled.Paths[0], 1, RuntimeMessageOutcome.Queued);
        var output = NodeView(snapshot, compiled, [record with { Revision = 6 },
            record with { Details = record.Details! with { PathKey = "other" } },
            record with { Details = record.Details! with { InputKey = "other/input" } }]).Single(node => node.Id == "out");
        Assert.Equal(0, output.ObservedPaths);
        Assert.Equal("waiting", output.State);
        Assert.Null(output.LastObservedAt);
    }

    [Fact]
    public void MissingCacheIsUnknownNotSuccessAndPartialObservationIsExplicit()
    {
        var snapshot = Snapshot(Route([Input(sources: "[1,2]"), Output()]), 2);
        var compiled = Compile(snapshot);
        var result = NodeView(snapshot, compiled, [NodeRecord(compiled.Paths[0], 1, RuntimeMessageOutcome.Queued)]);
        var output = result.Single(node => node.Id == "out");
        Assert.Equal("partialObservation", output.State);
        Assert.Equal(2, output.Paths);
        Assert.Equal(1, output.ObservedPaths);
    }

    [Theory]
    [InlineData(RuntimeUnitState.Backoff, "degraded", 1)]
    [InlineData(RuntimeUnitState.BuildFailed, "degraded", 1)]
    [InlineData(RuntimeUnitState.StopTimedOut, "degraded", 1)]
    [InlineData(RuntimeUnitState.Stopping, "sourceUnavailable", 0)]
    [InlineData(RuntimeUnitState.Stopped, "sourceUnavailable", 0)]
    public void StoppedOrFaultedInputsCannotBeHiddenByOldSuccessfulPathMessages(RuntimeUnitState state, string expected, int faults)
    {
        var snapshot = Snapshot();
        var compiled = Compile(snapshot);
        var status = NodeStatus(compiled);
        status = status with { Units = [status.Units[0] with { State = state, LastError = "private exception", LastErrorAt = ObservationTime }] };
        var result = NodeView(snapshot, compiled, [NodeRecord(compiled.Paths[0], 1, RuntimeMessageOutcome.Queued)], status);
        Assert.All(result, node => Assert.Equal(expected, node.State));
        Assert.All(result, node => Assert.Equal(faults, node.FaultedInputs));
        Assert.DoesNotContain("private", JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Fact]
    public void BackwardWallClockDoesNotReorderLatestInputOrOutputObservations()
    {
        var snapshot = Snapshot();
        var compiled = Compile(snapshot);
        var output = NodeRecord(compiled.Paths[0], 1, RuntimeMessageOutcome.MappingRejected, 0);
        var input = output with { ObjectId = RuntimeUnitId.Create("input.simulator", "d1/values"), Details = null };
        var result = NodeView(snapshot, compiled, [output, input,
            NodeRecord(compiled.Paths[0], 2, RuntimeMessageOutcome.Queued) with { CapturedAt = ObservationTime.AddHours(-1) },
            input with { Sequence = 3, CapturedAt = ObservationTime.AddHours(-2) }]);
        Assert.Equal("queued", result.Single(node => node.Id == "out").LastOutcome);
        Assert.Equal(ObservationTime.AddHours(-2), result.Single(node => node.Id == "in").LastObservedAt);
    }

    [Fact]
    public void OutputMetadataCannotAttributeAnotherNodesPathAndSharedSourceIsReadOnce()
    {
        var snapshot = Snapshot(Route([Input(), Input() with { Id = "other-in" }, Output(), Output() with { Id = "other-out" }],
            [new("in", "out"), new("other-in", "other-out")]));
        var compiled = Compile(snapshot);
        var path = compiled.Paths.Single(path => path.Path.Nodes[^1] == "out");
        var record = NodeRecord(path, 1, RuntimeMessageOutcome.Queued) with { ObjectId = RuntimeUnitId.Create("output.http", "route/other-out") };
        var reads = new Dictionary<RuntimeUnitId, int>();
        var result = RouteNodeRuntime.Read(compiled.Document, Checkpoint(snapshot), snapshot.Routes[0], NodeStatus(compiled), id =>
        {
            reads[id] = reads.GetValueOrDefault(id) + 1;
            return record.ObjectId == id ? [record] : [];
        }, Token);
        Assert.All(result.Where(node => node.Kind.StartsWith("output.", StringComparison.Ordinal)), node => Assert.Equal(0, node.ObservedPaths));
        Assert.Equal(1, reads[RuntimeUnitId.Create("input.simulator", "d1/values")]);
    }

    [Fact]
    public void DisabledRouteDoesNotReadMessagesAndCancellationIsHonored()
    {
        var snapshot = Snapshot();
        var compiled = Compile(snapshot);
        var result = RouteNodeRuntime.Read(compiled.Document, Checkpoint(snapshot), snapshot.Routes[0] with { Enabled = false },
            NodeStatus(compiled), _ => throw new InvalidOperationException("should not read"), Token);
        Assert.All(result, node => Assert.Equal("disabled", node.State));
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        Assert.Throws<OperationCanceledException>(() => RouteNodeRuntime.Read(compiled.Document, Checkpoint(snapshot), snapshot.Routes[0],
            NodeStatus(compiled), _ => [], stop.Token));
    }
}
