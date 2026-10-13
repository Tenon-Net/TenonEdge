using System.Text.Json;
using TenonEdge.Adapters.Http;
using TenonEdge.Adapters.Mqtt;
using TenonEdge.Host.Features.Configuration;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Routes;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Storage;

namespace TenonEdge.UnitTests.Routes;

public sealed class QueueOutputCompilationTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static RuntimePlanDocument Empty => RuntimePlanDocument.Parse("""{"schemaVersion":1,"units":[]}"""u8);
    private static ConnectionDraft Http(long id = 1) => new(id, "http" + id, "HTTP", ConnectionKind.Http, true, null, null,
        new("http://server.test/api/", Headers: new() { ["X-New"] = "not-frozen" }));
    private static ConnectionDraft Mqtt(long id = 2) => new(id, "mqtt" + id, "MQTT", ConnectionKind.Mqtt, true, null,
        new("broker.test", ClientId: "client" + id), null);
    private static RouteCompilationSnapshot Snapshot(params ConnectionDraft[] connections) => new("draft", [], new Dictionary<long, TenonEdge.Host.Features.Devices.DeviceDraft>(),
        new Dictionary<long, TenonEdge.Host.Features.Inputs.InputDraft>(), connections.ToDictionary(value => value.Id), new HashSet<long> { 2 });
    private static RuntimeSnapshotRecord Freeze(RuntimePlanDocument plan, params ConnectionDraft[] connections)
    {
        var snapshot = RuntimeSnapshotDocument.Create(plan, new("frozen", connections, [], [], [], [2]));
        return new() { SchemaVersion = snapshot.SchemaVersion, Content = snapshot.Content, ContentHash = snapshot.ContentHash };
    }

    [Fact]
    public void EnabledUnroutedTargetsHaveStableSenderIdsAndNoTopologyOrFormatting()
    {
        var snapshot = Snapshot(Http(), Mqtt(), Http(3) with { Enabled = false }, Mqtt(4) with { Enabled = false },
            new(5, "modbus", "Modbus", ConnectionKind.Modbus, true, new("device.test"), null, null));
        var compiled = RoutePlanCompiler.Compile(snapshot, Token);
        Assert.Empty(compiled.Topology.Paths);
        Assert.Empty(compiled.Topology.SourcePathCounts);
        Assert.Empty(compiled.Paths);
        Assert.Equal(2, compiled.Document.Units.Count);
        var http = Assert.Single(compiled.Document.Units, unit => unit.Id.Kind == "output.http");
        Assert.Equal("queue/http1/sender", http.Id.Key);
        var h = HttpOutputSettings.Parse(http.Configuration);
        Assert.True(h.QueueOnly);
        Assert.Empty(h.Inputs);
        Assert.Empty(h.Headers);
        Assert.Empty(h.Path);
        Assert.Equal("http://server.test/api/", h.BaseAddress);
        var mqtt = Assert.Single(compiled.Document.Units, unit => unit.Id.Kind == "output.mqtt");
        Assert.Equal("queue/mqtt2/sender", mqtt.Id.Key);
        var m = MqttOutputSettings.Parse(mqtt.Configuration);
        Assert.True(m.QueueOnly);
        Assert.True(m.UsePassword);
        Assert.Equal("client2", m.ClientId);
        Assert.Empty(m.Topic);
        Assert.Empty(m.Inputs);
        Assert.Equal(compiled.Document.ContentHash, RoutePlanCompiler.Compile(snapshot with
        { Connections = snapshot.Connections.Reverse().ToDictionary(), Version = "another" }, Token).Document.ContentHash);
    }

    [Fact]
    public void FrozenCheckpointAddsOnlyMissingSendersAndPreservesHistoricalBytesAndHash()
    {
        var raw = RuntimePlanDocument.Parse("""
            {"schemaVersion":1,"units":[{"kind":"output.http","key":"route/out",
            "configuration":{"connectionId":1,"baseAddress":"http://server.test/api/","path":"original","inputs":["meter/sample"]}}]}
            """u8);
        var snapshot = Freeze(raw, Http(), Mqtt());
        var before = (snapshot.Content, snapshot.ContentHash);
        var execution = RuntimeSnapshotDocument.ReadExecutionPlan(snapshot);
        Assert.Equal(2, execution.Units.Count);
        Assert.Equal(raw.Units[0].ConfigurationHash, execution.Units.Single(unit => unit.Id.Kind == "output.http").ConfigurationHash);
        Assert.True(MqttOutputSettings.Parse(execution.Units.Single(unit => unit.Id.Kind == "output.mqtt").Configuration).UsePassword);
        Assert.Equal(raw.ContentHash, RuntimeSnapshotDocument.ReadPlan(snapshot).ContentHash);
        Assert.NotEqual(raw.ContentHash, execution.ContentHash);
        Assert.Equal(before, (snapshot.Content, snapshot.ContentHash));
        Assert.Equal(execution.ContentHash, RuntimeSnapshotDocument.ReadExecutionPlan(snapshot).ContentHash);
        Assert.Equal(execution.ContentHash, RuntimeSnapshotDocument.ReadExecutionPlan(Freeze(execution, Http(), Mqtt())).ContentHash);
    }

    [Fact]
    public void NewCompilerAndFrozenCheckpointProduceTheSameExecutionPlan()
    {
        var compiled = RoutePlanCompiler.Compile(Snapshot(Http(), Mqtt()), Token).Document;
        Assert.Equal(compiled.ContentHash, RuntimeSnapshotDocument.ReadExecutionPlan(Freeze(Empty, Http(), Mqtt())).ContentHash);
        Assert.Equal(compiled.ContentHash, RuntimeSnapshotDocument.ReadExecutionPlan(Freeze(compiled, Http(), Mqtt())).ContentHash);
    }

    [Fact]
    public void HashCorruptionAndSeedSnapshotsNeverAcquireTargetsFromUnverifiedConfiguration()
    {
        var snapshot = Freeze(Empty, Http());
        snapshot.Content += " ";
        Assert.Throws<RuntimePlanDocumentException>(() => RuntimeSnapshotDocument.ReadExecutionPlan(snapshot));
        var seed = new RuntimeSnapshotRecord { SchemaVersion = 1, Content = Empty.CanonicalText, ContentHash = Empty.ContentHash };
        Assert.Empty(RuntimeSnapshotDocument.ReadExecutionPlan(seed).Units);
    }

    [Fact]
    public void MissingLegacyMqttCredentialReferencesAreNotGuessedAsAnonymous()
    {
        var frozen = RuntimeSnapshotDocument.Create(Empty, new("old", [Mqtt()], [], [], []));
        var snapshot = new RuntimeSnapshotRecord { SchemaVersion = 2, Content = frozen.Content, ContentHash = frozen.ContentHash };
        var queue = Assert.Single(RuntimeSnapshotDocument.ReadExecutionPlan(snapshot).Units);
        Assert.True(MqttOutputSettings.Parse(queue.Configuration).QueueCredentialReferenceMissing);
        Assert.Empty(RuntimeSnapshotDocument.ReadPlan(snapshot).Units);
    }

    [Fact]
    public void ConnectionLimitAndCancellationApplyEvenWithNoRoutes()
    {
        var snapshot = Snapshot(Enumerable.Range(1, 501).Select(i => Http(i)).ToArray());
        Assert.Equal("plan.configuration", Assert.Throws<RouteTopologyException>(() => RoutePlanCompiler.Compile(snapshot, Token)).Issue.Code);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => RoutePlanCompiler.Compile(Snapshot(Http()), cancelled.Token));
    }
}
