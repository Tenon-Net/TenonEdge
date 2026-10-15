using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Host.Features.Configuration;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Credentials;
using TenonEdge.Host.Features.Devices;
using TenonEdge.Host.Features.Inputs;
using TenonEdge.Host.Features.Routes;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Storage;
using TenonEdge.IntegrationTests.Infrastructure;

namespace TenonEdge.IntegrationTests.Runtime;

public sealed class RuntimeCheckpointTests
{
    [Fact]
    public async Task ApplicationFreezesUnusedAndDisabledDraftsAndRestartIgnoresLaterDraftEdits()
    {
        using var data = TemporaryDirectory.Create();
        string hash;
        long revision;
        await using (var factory = new FixedDataDirectoryApplicationFactory(data.Path))
        {
            using var client = factory.CreateClient();
            var connections = factory.Services.GetRequiredService<ConnectionDraftStore>();
            var devices = factory.Services.GetRequiredService<DeviceDraftStore>();
            var inputs = factory.Services.GetRequiredService<InputDraftStore>();
            var routes = factory.Services.GetRequiredService<RouteDraftStore>();
            var connection = connections.Write(null, new("0", "unused", "Unused", ConnectionKind.Http,
                Enabled: false, Http: new("http://localhost/")), "test", null);
            var device = devices.Write(null, new(connection.Version, "meter", "Meter", DeviceKind.Simulator, Enabled: false), "test", null);
            var input = inputs.Write(device.Device.Id, null, new(device.Version, "sample", "Sample", Enabled: false,
                Simulator: new(SampleJson: "{\"value\":42}")), "test", null);
            await factory.Services.GetRequiredService<HttpCredentialStore>().WriteAsync(connection.Connection.Id,
                new("http://localhost/", HttpCredentialKind.Bearer, Secret: "checkpoint-secret-not-for-snapshot", ExpectedDraftVersion: input.Version), "test", null, true, token: TestContext.Current.CancellationToken);
            var graph = new RouteGraph(1,
                [new("source", "input.simulator", 1, "Source", JsonSerializer.SerializeToElement(new { sourceIds = new[] { input.Input.Id } }))],
                [], new(new() { ["source"] = new(12, 34) }));
            var route = routes.Write(null, new(input.Version, "disabled", "Disabled", false, graph), "test", null);
            var versions = factory.Services.GetRequiredService<RuntimeVersionStore>();
            var coordinator = factory.Services.GetRequiredService<RuntimeCoordinator>();
            var applied = await coordinator.ApplyDraftAsync(route.Version, "test", null, TestContext.Current.CancellationToken);
            Assert.True(applied.Activated);
            revision = applied.Revision;
            var snapshot = versions.Load(revision)!;
            hash = snapshot.ContentHash;
            Assert.Equal(2, snapshot.SchemaVersion);
            Assert.DoesNotContain("checkpoint-secret-not-for-snapshot", snapshot.Content, StringComparison.Ordinal);
            Assert.DoesNotContain("protectedSecret", snapshot.Content, StringComparison.Ordinal);
            var (plan, draft) = RuntimeSnapshotDocument.Read(snapshot);
            Assert.Empty(plan.Units);
            Assert.NotNull(draft);
            Assert.Equal(route.Version, draft.Version);
            Assert.Equal(connection.Connection.Id, Assert.Single(draft.Connections).Id);
            Assert.Equal(device.Device.Id, Assert.Single(draft.Devices).Id);
            Assert.Equal(input.Input.Id, Assert.Single(draft.Inputs).Id);
            Assert.False(Assert.Single(draft.Routes).Enabled);
            Assert.Equal("Disabled", draft.Routes[0].Name);
            Assert.Equal(new RouteNodePosition(12, 34), draft.Routes[0].Graph.Layout.Nodes["source"]);
            Assert.Equal(input.Input.Id, draft.Routes[0].Graph.Nodes[0].Config.GetProperty("sourceIds")[0].GetInt64());
            routes.Write(route.Route.Id, new(route.Version, "disabled", "Edited after apply", false, new(1, [], [], new([]))), "test", null);
            Assert.Equal(hash, versions.Load(revision)!.ContentHash);
        }
        await using (var restarted = new FixedDataDirectoryApplicationFactory(data.Path))
        {
            using var client = restarted.CreateClient();
            var versions = restarted.Services.GetRequiredService<RuntimeVersionStore>();
            Assert.Equal(revision, restarted.Services.GetRequiredService<RuntimeCoordinator>().Runtime.ActiveRevision);
            Assert.Equal(hash, versions.Load(revision)!.ContentHash);
            Assert.Equal("Disabled", Assert.Single(RuntimeSnapshotDocument.Read(versions.Load(revision)!).Draft!.Routes).Name);
            Assert.Equal("Edited after apply", Assert.Single(restarted.Services.GetRequiredService<RouteDraftStore>().Read().Routes).Name);
        }
    }

    [Fact]
    public async Task RollbackFreezePreservesWholeCheckpointWithoutReplacingCurrentDraft()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        using var client = factory.CreateClient();
        var versions = factory.Services.GetRequiredService<RuntimeVersionStore>();
        var database = factory.Services.GetRequiredService<ConfigDatabase>();
        var plan = RuntimePlanDocument.Parse("""{"schemaVersion":1,"units":[]}"""u8);
        var first = versions.FreezeDraft(plan, "0", "test");
        versions.Commit(first, null);
        var target = versions.Load(first)!;
        var changed = database.InTransaction(db => ConfigurationDraft.Advance(db, TimeProvider.System));
        var rollback = versions.FreezeRollback(target, RuntimeVersionStore.SystemOperator);
        Assert.Equal(target.Content, versions.Load(rollback)!.Content);
        Assert.Equal(target.ContentHash, versions.Load(rollback)!.ContentHash);
        Assert.Equal(2, versions.Load(rollback)!.SchemaVersion);
        versions.Commit(rollback, null);
        Assert.Equal(changed, database.InTransaction(db => ConfigurationDraft.ReadVersion(db)));
    }

    [Fact]
    public async Task StartupProtectionPreservesVersionTwoCheckpointAndLeavesEditedDraftAlone()
    {
        using var data = TemporaryDirectory.Create();
        string edited;
        await using (var factory = new FixedDataDirectoryApplicationFactory(data.Path))
        {
            using var client = factory.CreateClient();
            var coordinator = factory.Services.GetRequiredService<RuntimeCoordinator>();
            var versions = factory.Services.GetRequiredService<RuntimeVersionStore>();
            await coordinator.ApplyDraftAsync("0", "test", null, TestContext.Current.CancellationToken);
            Assert.True(versions.MarkStable(1));
            var database = factory.Services.GetRequiredService<ConfigDatabase>();
            edited = database.InTransaction(db => ConfigurationDraft.Advance(db, TimeProvider.System));
            await coordinator.ApplyDraftAsync(edited, "test", null, TestContext.Current.CancellationToken);
        }
        var state = new RuntimeStateReader(data.Path);
        state.Execute("UPDATE runtime_snapshot SET abnormal_exit_count = 2 WHERE revision = 2; UPDATE system_state SET running_revision = 2;");
        await using var restarted = new FixedDataDirectoryApplicationFactory(data.Path);
        using var restartedClient = restarted.CreateClient();
        var store = restarted.Services.GetRequiredService<RuntimeVersionStore>();
        Assert.Equal(3, restarted.Services.GetRequiredService<RuntimeCoordinator>().Runtime.ActiveRevision);
        Assert.Equal(store.Load(1)!.Content, store.Load(3)!.Content);
        Assert.Equal("0", RuntimeSnapshotDocument.Read(store.Load(3)!).Draft!.Version);
        Assert.Equal(edited, restarted.Services.GetRequiredService<RouteDraftStore>().Read().Version);
    }

    [Fact]
    public void CompleteEnvelopeHasItsOwnSizeLimitIncludingDisabledObjects()
    {
        var plan = RuntimePlanDocument.Parse("""{"schemaVersion":1,"units":[]}"""u8);
        var draft = new ConfigurationCheckpoint("0", [], [], [],
            [new(1, "disabled", new string('x', (int)RuntimePlanDocument.MaximumBytes), null, false, new(1, [], [], new([])), null, 0)]);
        Assert.Throws<RuntimePlanDocumentException>(() => RuntimeSnapshotDocument.Create(plan, draft));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NeverEffectiveCheckpointsCannotBeRollbackTargets(bool abandon)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var store = factory.Services.GetRequiredService<RuntimeVersionStore>();
        var revision = store.FreezeDraft(RuntimePlanDocument.Parse("""{"schemaVersion":1,"units":[]}"""u8), "0", "test");
        if (abandon)
        { store.Abandon(revision, "fixture"); }
        Assert.Throws<InvalidOperationException>(() => store.FreezeRollback(store.Load(revision)!, "test"));
        Assert.Null(store.Load(revision + 1));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CheckpointTamperingIsRejectedBeforeCommit(bool recomputeHash)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var versions = factory.Services.GetRequiredService<RuntimeVersionStore>();
        var database = factory.Services.GetRequiredService<ConfigDatabase>();
        var revision = versions.FreezeDraft(RuntimePlanDocument.Parse("""{"schemaVersion":1,"units":[]}"""u8), "0", "test");
        var snapshot = versions.Load(revision)!;
        snapshot.Content = snapshot.Content.Replace("\"draft\":{", "\"unexpected\":{", StringComparison.Ordinal);
        if (recomputeHash)
        { snapshot.ContentHash = RuntimePlanDocument.HashOf(snapshot.Content); }
        database.Client.Updateable(snapshot).ExecuteCommand();
        Assert.Throws<RuntimePlanDocumentException>(() => versions.Commit(revision, null));
        Assert.Null(versions.ReadPointers().EffectiveRevision);
        Assert.Equal(revision, versions.ReadPointers().PendingRevision);
    }
}
