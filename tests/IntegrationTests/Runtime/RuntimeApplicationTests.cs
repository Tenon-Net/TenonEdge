using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using TenonEdge.Host.Bootstrap;
using TenonEdge.Host.Features.Audit;
using TenonEdge.Host.Features.Configuration;
using TenonEdge.Host.Features.Routes;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Storage;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.Runtime.Lifecycle;

namespace TenonEdge.IntegrationTests.Runtime;

public sealed class RuntimeApplicationTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ApplyingDraftsCommitsAuditAndRunningMarkerAndRestartsStabilityForEveryRevision()
    {
        using var data = TemporaryDirectory.Create();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var factory = new FixedDataDirectoryApplicationFactory(data.Path, configureServices: services =>
        {
            services.RemoveAll<RuntimeCoordinator>();
            services.AddSingleton(provider => new RuntimeCoordinator(
                provider.GetRequiredService<RuntimeHost>(), provider.GetRequiredService<RuntimeVersionStore>(),
                provider.GetRequiredService<RouteDraftStore>(), provider.GetRequiredService<RuntimeSettings>(),
                provider.GetRequiredService<RuntimeStartConditions>(), provider.GetRequiredService<RuntimeEventLog>(),
                clock, provider.GetRequiredService<ILogger<RuntimeCoordinator>>()));
        });
        using var client = factory.CreateClient();
        var coordinator = factory.Services.GetRequiredService<RuntimeCoordinator>();
        var versions = factory.Services.GetRequiredService<RuntimeVersionStore>();
        var drafts = factory.Services.GetRequiredService<RouteDraftStore>();
        var database = factory.Services.GetRequiredService<ConfigDatabase>();
        var first = await coordinator.ApplyDraftAsync(drafts.Read().Version, "operator", "127.0.0.1", Token);
        Assert.True(first.Committed && first.Activated);
        Assert.Equal(first.Revision, versions.ReadPointers().RunningRevision);
        Assert.Equal(first.Revision, coordinator.Runtime.ActiveRevision);
        clock.Advance(TimeSpan.FromSeconds(30));
        var second = await coordinator.ApplyDraftAsync(drafts.Read().Version, "operator", "127.0.0.1", Token);
        Assert.Equal(first.Revision + 1, second.Revision);
        Assert.Equal(first.Revision, versions.ReadPointers().PreviousRevision);
        Assert.Equal(second.Revision, versions.ReadPointers().RunningRevision);
        Assert.Null(versions.ReadPointers().PendingRevision);
        Assert.False(versions.MarkStable(first.Revision));
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.False(versions.Load(second.Revision)!.IsStable);
        clock.Advance(TimeSpan.FromSeconds(30));
        await OutputPipelineTests.UntilAsync(() => versions.Load(second.Revision)!.IsStable);
        Assert.False(versions.Load(first.Revision)!.IsStable);
        var third = await coordinator.ApplyDraftAsync(drafts.Read().Version, "operator", null, Token);
        clock.Advance(TimeSpan.FromSeconds(60));
        await OutputPipelineTests.UntilAsync(() => versions.Load(third.Revision)!.IsStable);
        Assert.False(coordinator.ExecuteTask!.IsCompleted);
        var audit = database.Client.Queryable<AuditLogRecord>().Where(row => row.Action == AuditActions.SnapshotApplied).ToList();
        Assert.Equal(3, audit.Count);
        Assert.All(audit, row => Assert.Equal("operator", row.AccountName));
        await coordinator.StopAsync(Token);
        Assert.Null(versions.ReadPointers().RunningRevision);
        Assert.Equal(RuntimeCoordinatorState.Stopped, coordinator.Status.State);
    }

    [Fact]
    public async Task StaleAndPreCancelledApplicationsDoNotAllocateVersions()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        using var client = factory.CreateClient();
        var coordinator = factory.Services.GetRequiredService<RuntimeCoordinator>();
        var versions = factory.Services.GetRequiredService<RuntimeVersionStore>();
        await Assert.ThrowsAsync<DraftWriteException>(() => coordinator.ApplyDraftAsync("stale", "operator", null, Token));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await coordinator.ApplyDraftAsync("0", "operator", null, cancelled.Token));
        Assert.Null(versions.ReadPointers().EffectiveRevision);
        Assert.Null(versions.ReadPointers().PendingRevision);
        Assert.Equal(0, factory.Services.GetRequiredService<ConfigDatabase>().Client.Queryable<RuntimeSnapshotRecord>().Count());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommitStorageFailureAbandonsOnlyNewVersionAndLeavesOriginalRunningMarker(bool auditFailure)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        using var client = factory.CreateClient();
        var coordinator = factory.Services.GetRequiredService<RuntimeCoordinator>();
        var versions = factory.Services.GetRequiredService<RuntimeVersionStore>();
        var database = factory.Services.GetRequiredService<ConfigDatabase>();
        var first = await coordinator.ApplyDraftAsync("0", "operator", null, Token);
        database.Client.Ado.ExecuteCommand(auditFailure
            ? "CREATE TRIGGER fail_online_commit BEFORE INSERT ON audit_log WHEN NEW.action = 'runtime.apply' AND NEW.target = 'r2' BEGIN SELECT RAISE(ABORT, 'fixture-audit-fault'); END"
            : "CREATE TRIGGER fail_online_commit BEFORE UPDATE OF effective_revision ON system_state WHEN NEW.effective_revision = 2 BEGIN SELECT RAISE(ABORT, 'fixture-commit-fault'); END");
        try
        {
            var failed = await coordinator.ApplyDraftAsync("0", "operator", null, Token);
            Assert.False(failed.Committed);
            Assert.Equal("runtime.commitFailed", failed.Code);
            Assert.Equal(first.Revision, versions.ReadPointers().EffectiveRevision);
            Assert.Equal(first.Revision, versions.ReadPointers().RunningRevision);
            Assert.Null(versions.ReadPointers().PendingRevision);
            Assert.Equal(RuntimeSnapshotStates.NotEffective, versions.Load(failed.Revision)!.State);
            Assert.Equal(first.Revision, coordinator.Runtime.ActiveRevision);
            Assert.Equal(first.Revision, coordinator.Status.ActiveRevision);
            Assert.Single(database.Client.Queryable<AuditLogRecord>().Where(row => row.Action == AuditActions.SnapshotApplied).ToList());
        }
        finally { database.Client.Ado.ExecuteCommand("DROP TRIGGER fail_online_commit"); }
        var repaired = await coordinator.ApplyDraftAsync("0", "operator", null, Token);
        Assert.True(repaired.Activated);
        Assert.Equal(3, repaired.Revision);
    }
}
