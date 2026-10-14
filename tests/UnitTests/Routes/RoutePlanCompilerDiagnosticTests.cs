using System.Text;
using TenonEdge.Abstractions.Plans;
using TenonEdge.Host.Features.Configuration;
using TenonEdge.Host.Features.Routes;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Storage;

namespace TenonEdge.UnitTests.Routes;

public sealed partial class RoutePlanCompilerTests
{
    [Fact]
    public void RejoinedOutputHasOneDiagnosticIdentityWithoutChangingCompiledUnitIdsOrFrozenBytes()
    {
        var route = Route([Input(sources: "[2,1]"), Map("a", "value", "a"), Map("b", "value", "b"), Output()],
            [new("in", "a"), new("in", "b"), new("a", "out"), new("b", "out")]);
        var snapshot = Snapshot(route, 2);
        var document = Compile(snapshot).Document;
        var plan = document.ToPlan(7);
        Assert.Equal(2, plan.DiagnosticObjects.Count);
        var node = RuntimeUnitId.Create("output.http", "route/out");
        Assert.All(plan.DiagnosticObjects, pair => Assert.Equal(node, pair.Value));
        Assert.All(plan.Units.Where(unit => unit.Id.Kind == "input.simulator"), unit => Assert.Equal(unit.Id, plan.DiagnosticObject(unit.Id)));
        // On disk the plan still has exactly the original schema, keys and hash.
        var parsed = RuntimePlanDocument.Parse(Encoding.UTF8.GetBytes(document.CanonicalText));
        Assert.Empty(parsed.ToPlan(7).DiagnosticObjects);
        Assert.Equal(document.ContentHash, parsed.ContentHash);
        var checkpoint = new ConfigurationCheckpoint(snapshot.Version, snapshot.Connections.Values.ToArray(), snapshot.Devices.Values.ToArray(),
            snapshot.Inputs.Values.ToArray(), snapshot.Routes.ToArray(), []);
        var frozen = RuntimeSnapshotDocument.Create(parsed, checkpoint);
        var record = new RuntimeSnapshotRecord { Revision = 7, SchemaVersion = 2, Content = frozen.Content, ContentHash = frozen.ContentHash };
        var reloaded = RuntimeSnapshotDocument.ReadExecutionPlan(record).ToPlan(7);
        Assert.Equal(plan.DiagnosticObjects.OrderBy(pair => pair.Key.Key), reloaded.DiagnosticObjects.OrderBy(pair => pair.Key.Key));
        Assert.Equal(frozen.ContentHash, record.ContentHash);
        Assert.Equal(frozen.Content, record.Content);
        Assert.Equal(document.CanonicalText, RuntimeSnapshotDocument.ReadPlan(record).CanonicalText);
    }

    [Fact]
    public void MissingHistoricalTopologyDoesNotChangeExecutionAndUsesConservativeSharedBucket()
    {
        var snapshot = Snapshot();
        var document = Compile(snapshot).Document;
        var checkpoint = new ConfigurationCheckpoint(snapshot.Version, snapshot.Connections.Values.ToArray(), snapshot.Devices.Values.ToArray(),
            snapshot.Inputs.Values.ToArray(), [], []);
        var annotated = RoutePlanCompiler.WithOutputDiagnosticObjects(document, checkpoint);
        Assert.Equal(document.ContentHash, annotated.ContentHash);
        Assert.Equal(document.CanonicalText, annotated.CanonicalText);
        Assert.Equal("unresolved", Assert.Single(annotated.ToPlan(7).DiagnosticObjects).Value.Key);
        // Unknown future module metadata must not globally stop the existing plan.
        checkpoint = checkpoint with { Routes = [snapshot.Routes[0] with { Graph = snapshot.Routes[0].Graph with { Nodes = [Input(), Node("out", "output.unknown", "{}")] } }] };
        Assert.Equal("unresolved", Assert.Single(RoutePlanCompiler.WithOutputDiagnosticObjects(document, checkpoint).ToPlan(7).DiagnosticObjects).Value.Key);
    }

    [Fact]
    public void DiagnosticMappingIsImmutableAndCannotRelabelInputsUnknownUnitsOrProtocols()
    {
        var document = Compile(Snapshot()).Document;
        var output = document.Units.Single(unit => unit.Id.Kind == "output.http").Id;
        var input = document.Units.Single(unit => unit.Id.Kind.StartsWith("input.", StringComparison.Ordinal)).Id;
        var node = RuntimeUnitId.Create("output.http", "route/out");
        var labels = new Dictionary<RuntimeUnitId, RuntimeUnitId> { [output] = node };
        var plan = new RuntimePlan(1, document.Units, labels);
        labels.Clear();
        Assert.Equal(node, plan.DiagnosticObject(output));
        Assert.Throws<ArgumentException>(() => new RuntimePlan(1, document.Units, new Dictionary<RuntimeUnitId, RuntimeUnitId> { [input] = input }));
        Assert.Throws<ArgumentException>(() => new RuntimePlan(1, document.Units, new Dictionary<RuntimeUnitId, RuntimeUnitId> { [node] = node }));
        Assert.Throws<ArgumentException>(() => new RuntimePlan(1, document.Units, new Dictionary<RuntimeUnitId, RuntimeUnitId> { [output] = RuntimeUnitId.Create("output.mqtt", "route/out") }));
    }
}
