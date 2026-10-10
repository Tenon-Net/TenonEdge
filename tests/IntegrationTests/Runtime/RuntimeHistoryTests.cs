using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Host.Features.Audit;
using TenonEdge.Host.Features.Configuration;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Devices;
using TenonEdge.Host.Features.Inputs;
using TenonEdge.Host.Features.Routes;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Storage;
using TenonEdge.IntegrationTests.Infrastructure;

namespace TenonEdge.IntegrationTests.Runtime;

public sealed class RuntimeHistoryTests
{
    private static RuntimePlanDocument Plan() => RuntimePlanDocument.Parse("""{"schemaVersion":1,"units":[]}"""u8);

    [Fact]
    public async Task RetainsTwentyEffectiveAndTwentyFailedVersionsWithMonotonicCursorHistory()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var store = factory.Services.GetRequiredService<RuntimeVersionStore>();
        for (var index = 0; index < 21; index++)
        {
            var revision = store.FreezeDraft(Plan(), "0", "operator");
            store.Commit(revision, null, markRunning: true);
        }
        Assert.Null(store.Load(1));
        Assert.Equal(20, store.ReadHistory(limit: 100).Length);
        Assert.Equal(21, store.ReadPointers().EffectiveRevision);
        Assert.Equal(20, store.ReadPointers().PreviousRevision);
        for (var index = 0; index < 23; index++)
        {
            var revision = store.FreezeDraft(Plan(), "0", "operator");
            store.Abandon(revision, "fixture");
        }
        var pending = store.FreezeDraft(Plan(), "0", "operator");
        Assert.Equal(45, pending);
        Assert.Equal(41, store.ReadHistory(limit: 100).Length);
        Assert.Equal(pending, store.DiscardInterruptedPending());
        var history = store.ReadHistory(limit: 100);
        Assert.Equal(40, history.Length);
        Assert.Equal(20, history.Count(row => row.State == RuntimeSnapshotStates.NotEffective));
        Assert.Equal(20, history.Count(row => row.ActivatedAt is not null));
        var firstPage = store.ReadHistory(limit: 7);
        var secondPage = store.ReadHistory(firstPage[^1].Revision, limit: 100);
        Assert.Equal(history.Select(row => row.Revision), firstPage.Concat(secondPage).Select(row => row.Revision));
        Assert.Equal(RuntimeVersionStore.InterruptedReason, firstPage[0].NotEffectiveReason);
        Assert.Equal(46, store.FreezeDraft(Plan(), "0", "operator"));
    }

    [Fact]
    public async Task PruningFailureRollsBackPointersStateAndAuditTogether()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var store = factory.Services.GetRequiredService<RuntimeVersionStore>();
        var database = factory.Services.GetRequiredService<ConfigDatabase>();
        for (var index = 0; index < 20; index++)
        { store.Commit(store.FreezeDraft(Plan(), "0", "operator"), null, markRunning: true); }
        var next = store.FreezeDraft(Plan(), "0", "operator");
        database.Client.Ado.ExecuteCommand("CREATE TRIGGER deny_prune BEFORE DELETE ON runtime_snapshot BEGIN SELECT RAISE(ABORT, 'fixture-prune'); END");
        try
        {
            Assert.ThrowsAny<Exception>(() => store.Commit(next,
                new AuditEntry(AuditActions.SnapshotApplied, AuditResults.Success, "operator", null, "r21", "fixture"), markRunning: true));
            Assert.Equal(20, store.ReadPointers().EffectiveRevision);
            Assert.Equal(20, store.ReadPointers().RunningRevision);
            Assert.Equal(21, store.ReadPointers().PendingRevision);
            Assert.Equal(RuntimeSnapshotStates.Effective, store.Load(20)!.State);
            Assert.NotNull(store.Load(1));
            Assert.Empty(database.Client.Queryable<AuditLogRecord>().Where(row => row.Action == AuditActions.SnapshotApplied).ToList());
        }
        finally { database.Client.Ado.ExecuteCommand("DROP TRIGGER deny_prune"); }
        store.Commit(next, null, markRunning: true);
        Assert.Null(store.Load(1));
    }

    [Fact]
    public async Task HistoryComparisonUsesFrozenDraftsAndDetectsStaleDraftRequests()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var store = factory.Services.GetRequiredService<RuntimeVersionStore>();
        var devices = factory.Services.GetRequiredService<DeviceDraftStore>();
        var first = store.FreezeDraft(Plan(), "0", "operator");
        store.Commit(first, null);
        var device = devices.Write(null, new("0", "meter", "Meter", DeviceKind.Simulator), "operator", null);
        var second = store.FreezeDraft(Plan(), device.Version, "operator");
        store.Commit(second, null);
        var change = Assert.Single(store.CompareVersions(first, second));
        Assert.Equal(("device", device.Device.Id, "added"), (change.Kind, change.Id, change.Change));
        Assert.Null(change.Before);
        Assert.Equal("Meter", change.After!.Value.GetProperty("name").GetString());
        Assert.Empty(store.CompareDraft(second, device.Version));
        var edited = devices.Write(device.Device.Id, new(device.Version, "meter", "Edited", DeviceKind.Simulator), "operator", null);
        Assert.Throws<DraftWriteException>(() => store.CompareDraft(second, device.Version));
        Assert.Equal("modified", Assert.Single(store.CompareDraft(second, edited.Version)).Change);
        Assert.Equal("Meter", Assert.Single(store.CompareVersions(first, second)).After!.Value.GetProperty("name").GetString());
        Assert.Equal("removed", Assert.Single(store.CompareVersions(second, first)).Change);
        Assert.Equal("added", Assert.Single(store.CompareDraft(null, edited.Version)).Change);
        Assert.Throws<KeyNotFoundException>(() => store.CompareVersions(first, 999));
    }

    [Fact]
    public async Task LegacyHistoryIsListedButNotInventedAsAnEmptyDraft()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var store = factory.Services.GetRequiredService<RuntimeVersionStore>();
        var revision = store.Freeze(Plan(), RuntimeSnapshotOrigins.Seed, null, "system");
        store.Commit(revision, null);
        Assert.Equal(1, Assert.Single(store.ReadHistory()).SchemaVersion);
        Assert.Throws<NotSupportedException>(() => store.CompareDraft(revision, "0"));
        Assert.Throws<NotSupportedException>(() => store.CompareVersions(revision, revision));
    }

    [Fact]
    public void ComparisonIgnoresVersionsMetadataAndObjectPropertyOrderButPreservesLayoutChanges()
    {
        var graph = new RouteGraph(1, [new("node", "input.simulator", 1, "Source", JsonSerializer.SerializeToElement(new { a = 1, b = 2 }))],
            [], new(new() { ["node"] = new(1, 2) }));
        var route = new RouteDraft(7, "route", "Route", null, false, graph, "one", 100);
        var before = new ConfigurationCheckpoint("v1", [], [], [], [route]);
        var reordered = graph with { Nodes = [graph.Nodes[0] with { Config = JsonSerializer.SerializeToElement(new { b = 2, a = 1 }) }] };
        var after = before with { Version = "v2", Routes = [route with { UpdatedBy = "two", UpdatedAt = 200, Graph = reordered }] };
        Assert.Empty(ConfigurationDiff.Between(before, after));
        after = after with { Routes = [after.Routes[0] with { Graph = reordered with { Layout = new(new() { ["node"] = new(9, 2) }) } }] };
        Assert.Equal(("route", "modified"), (Assert.Single(ConfigurationDiff.Between(before, after)).Kind, ConfigurationDiff.Between(before, after)[0].Change));
    }

    [Fact]
    public void SameKeyWithDifferentStableIdsIsRemovalAndAdditionNotAModification()
    {
        var connection = new ConnectionDraft(1, "same", "Same", ConnectionKind.Http, false, null, null, new("http://localhost/"));
        var before = new ConfigurationCheckpoint("v1", [connection], [], [], []);
        var after = before with { Connections = [connection with { Id = 2 }] };
        var changes = ConfigurationDiff.Between(before, after);
        Assert.Equal(2, changes.Length);
        Assert.Equal("removed", changes[0].Change);
        Assert.Equal("added", changes[1].Change);
    }

    [Fact]
    public void InputChangesCarryDeviceIdentityAndBothFrozenFieldDefinitions()
    {
        var input = new InputDraft(9, 8, "sample", "Sample", DeviceKind.Simulator, false, null,
            Simulator: new(SampleJson: "{\"value\":1}"));
        var before = new ConfigurationCheckpoint("v1", [], [], [input], []);
        var after = before with { Inputs = [input with { Enabled = true, Simulator = new(SampleJson: "{\"value\":2}") }] };
        var change = Assert.Single(ConfigurationDiff.Between(before, after));
        Assert.Equal(("input", 9L, (long?)8, "modified"), (change.Kind, change.Id, change.DeviceId, change.Change));
        Assert.False(change.Before!.Value.GetProperty("enabled").GetBoolean());
        Assert.True(change.After!.Value.GetProperty("enabled").GetBoolean());
        Assert.Equal("{\"value\":1}", change.Before.Value.GetProperty("simulator").GetProperty("sampleJson").GetString());
        Assert.Equal("{\"value\":2}", change.After.Value.GetProperty("simulator").GetProperty("sampleJson").GetString());
    }

    [Theory]
    [InlineData(0, 20)]
    [InlineData(1, 0)]
    [InlineData(1, 101)]
    public async Task HistoryPagingIsBounded(long cursor, int limit)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        Assert.Throws<ArgumentOutOfRangeException>(() => factory.Services.GetRequiredService<RuntimeVersionStore>().ReadHistory(cursor, limit));
    }
}
