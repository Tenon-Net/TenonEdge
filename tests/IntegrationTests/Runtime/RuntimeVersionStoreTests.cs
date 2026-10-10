using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Host.Features.Audit;
using TenonEdge.Host.Features.Configuration;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Storage;
using TenonEdge.IntegrationTests.Infrastructure;

namespace TenonEdge.IntegrationTests.Runtime;

public sealed class RuntimeVersionStoreTests
{
    private static string ReadDraftVersion(ConfigDatabase database)
    {
        using var client = database.CreateIsolatedClient();
        return ConfigurationDraft.ReadVersion(client);
    }

    private static RuntimePlanDocument Document() => RuntimePlanDocument.Parse("""{"schemaVersion":1,"units":[]}"""u8);

    [Fact]
    public async Task SecondFreezeCannotReplaceAnOwnedPendingRevision()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var versions = factory.Services.GetRequiredService<RuntimeVersionStore>();
        var first = versions.Freeze(Document(), RuntimeSnapshotOrigins.Seed, null, "first");
        Assert.ThrowsAny<InvalidOperationException>(() => versions.Freeze(Document(), RuntimeSnapshotOrigins.Seed, null, "second"));
        Assert.Equal(first, versions.ReadPointers().PendingRevision);
        Assert.Null(versions.Load(first + 1));
    }

    [Fact]
    public async Task StaleAbandonCannotTouchAnotherOperationOrSeedMarker()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var versions = factory.Services.GetRequiredService<RuntimeVersionStore>();
        var old = versions.Freeze(Document(), RuntimeSnapshotOrigins.Seed, null, "first");
        versions.Abandon(old, "fixture");
        var current = versions.Freeze(Document(), RuntimeSnapshotOrigins.Seed, null, "second");
        var before = versions.ReadPointers();
        Assert.ThrowsAny<InvalidOperationException>(() => versions.Abandon(old, "stale", "must-not-change"));
        Assert.Equal(before, versions.ReadPointers());
        Assert.Equal(RuntimeSnapshotStates.Pending, versions.Load(current)!.State);
    }

    [Fact]
    public async Task DraftFreezeChecksConfirmedVersionAndAllowsLaterEditsWithoutChangingFrozenContent()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var versions = factory.Services.GetRequiredService<RuntimeVersionStore>();
        var database = factory.Services.GetRequiredService<ConfigDatabase>();
        var confirmed = ReadDraftVersion(database);
        var pointers = versions.ReadPointers();
        Assert.Equal("configuration.draftConflict", Assert.Throws<DraftWriteException>(() => versions.FreezeDraft(Document(), "stale", "operator")).Code);
        Assert.Equal(pointers, versions.ReadPointers());
        Assert.Empty(database.Client.Queryable<RuntimeSnapshotRecord>().ToList());
        var revision = versions.FreezeDraft(Document(), confirmed, "operator");
        var frozen = versions.Load(revision)!;
        Assert.Equal(RuntimeSnapshotOrigins.Apply, frozen.Origin);
        Assert.Equal("operator", frozen.CreatedBy);
        Assert.Equal(Document().CanonicalText, frozen.Content);
        Assert.Equal(Document().ContentHash, frozen.ContentHash);
        var changed = database.InTransaction(client => ConfigurationDraft.Advance(client, TimeProvider.System));
        versions.Commit(revision, null);
        Assert.Equal(changed, ReadDraftVersion(database));
        Assert.Equal(frozen.ContentHash, versions.Load(revision)!.ContentHash);
        Assert.Equal(revision, versions.ReadPointers().EffectiveRevision);
        Assert.Throws<ArgumentException>(() => versions.Freeze(Document(), RuntimeSnapshotOrigins.Apply, null, "bypass"));
    }

    [Fact]
    public async Task ConcurrentFreezeHasExactlyOneWinnerAndDoesNotConsumeAnotherRevision()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var versions = factory.Services.GetRequiredService<RuntimeVersionStore>();
        var database = factory.Services.GetRequiredService<ConfigDatabase>();
        var confirmed = ReadDraftVersion(database);
        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
        {
            try
            { return (Revision: (long?)versions.FreezeDraft(Document(), confirmed, "operator"), Error: (RuntimeVersionConflictException?)null); }
            catch (RuntimeVersionConflictException error) { return (Revision: (long?)null, Error: error); }
        }, TestContext.Current.CancellationToken)));
        var winner = Assert.Single(results, result => result.Revision is not null).Revision!.Value;
        var conflict = Assert.Single(results, result => result.Error is not null).Error!;
        Assert.Equal("runtime.applicationInProgress", conflict.Code);
        Assert.Equal(winner, conflict.Revision);
        Assert.Equal(winner, versions.ReadPointers().PendingRevision);
        Assert.Single(database.Client.Queryable<RuntimeSnapshotRecord>().ToList());
        versions.Abandon(winner, "fixture");
        Assert.Equal(winner + 1, versions.FreezeDraft(Document(), confirmed, "operator"));
    }

    [Fact]
    public async Task AStrandedPendingSnapshotCannotCommitWithoutOwningThePointer()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var versions = factory.Services.GetRequiredService<RuntimeVersionStore>();
        var database = factory.Services.GetRequiredService<ConfigDatabase>();
        var revision = versions.Freeze(Document(), RuntimeSnapshotOrigins.Seed, null, "fixture");
        // Reconstruct a stale checkpoint that older code could leave when pending was overwritten.
        database.Client.Ado.ExecuteCommand("UPDATE system_state SET pending_revision = NULL WHERE id = 1");
        var error = Assert.Throws<RuntimeVersionConflictException>(() => versions.Commit(revision, null));
        Assert.Equal("runtime.pendingMismatch", error.Code);
        Assert.Null(versions.ReadPointers().EffectiveRevision);
        Assert.Equal(RuntimeSnapshotStates.Pending, versions.Load(revision)!.State);
    }

    [Fact]
    public async Task FreezeFailureRollsBackSnapshotPointerAndRevisionFloorTogether()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var versions = factory.Services.GetRequiredService<RuntimeVersionStore>();
        var database = factory.Services.GetRequiredService<ConfigDatabase>();
        var confirmed = ReadDraftVersion(database);
        database.Client.Ado.ExecuteCommand("CREATE TRIGGER fail_pending BEFORE UPDATE OF pending_revision ON system_state WHEN NEW.pending_revision IS NOT NULL BEGIN SELECT RAISE(ABORT, 'fixture-freeze-fault'); END");
        try
        {
            Assert.ThrowsAny<Exception>(() => versions.FreezeDraft(Document(), confirmed, "operator"));
            Assert.Empty(database.Client.Queryable<RuntimeSnapshotRecord>().ToList());
            Assert.Null(versions.ReadPointers().PendingRevision);
            Assert.Equal(0, database.Client.Queryable<SystemStateRecord>().InSingle(1).RevisionFloor);
        }
        finally { database.Client.Ado.ExecuteCommand("DROP TRIGGER fail_pending"); }
        Assert.Equal(1, versions.FreezeDraft(Document(), confirmed, "operator"));
    }

    [Fact]
    public async Task CommitAndAuditAreAtomicAndSuccessfulCommitMovesAllPointers()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var versions = factory.Services.GetRequiredService<RuntimeVersionStore>();
        var database = factory.Services.GetRequiredService<ConfigDatabase>();
        var confirmed = ReadDraftVersion(database);
        var old = versions.FreezeDraft(Document(), confirmed, "old");
        versions.Commit(old, null);
        var revision = versions.FreezeDraft(Document(), confirmed, "new");
        var audit = new AuditEntry("fixture.apply", AuditResults.Success, "operator", null);
        database.Client.Ado.ExecuteCommand("CREATE TRIGGER fail_apply_audit BEFORE INSERT ON audit_log WHEN NEW.action = 'fixture.apply' BEGIN SELECT RAISE(ABORT, 'fixture-commit-fault'); END");
        try
        {
            Assert.ThrowsAny<Exception>(() => versions.Commit(revision, audit));
            Assert.Equal(old, versions.ReadPointers().EffectiveRevision);
            Assert.Equal(revision, versions.ReadPointers().PendingRevision);
            Assert.Equal(RuntimeSnapshotStates.Effective, versions.Load(old)!.State);
            Assert.Equal(RuntimeSnapshotStates.Pending, versions.Load(revision)!.State);
        }
        finally { database.Client.Ado.ExecuteCommand("DROP TRIGGER fail_apply_audit"); }
        versions.Commit(revision, audit);
        Assert.Equal(old, versions.ReadPointers().PreviousRevision);
        Assert.Equal(revision, versions.ReadPointers().EffectiveRevision);
        Assert.Null(versions.ReadPointers().PendingRevision);
        Assert.Equal(RuntimeSnapshotStates.History, versions.Load(old)!.State);
        Assert.Equal(RuntimeSnapshotStates.Effective, versions.Load(revision)!.State);
        Assert.NotNull(versions.Load(revision)!.ActivatedAt);
        Assert.Single(TestAccounts.Audit(factory.Services, "fixture.apply"));
        Assert.Throws<RuntimeVersionConflictException>(() => versions.Commit(revision, audit));
        Assert.Single(TestAccounts.Audit(factory.Services, "fixture.apply"));
    }

    [Fact]
    public async Task RevisionFloorIsRespectedAndExhaustionNeverWraps()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var versions = factory.Services.GetRequiredService<RuntimeVersionStore>();
        var database = factory.Services.GetRequiredService<ConfigDatabase>();
        var confirmed = ReadDraftVersion(database);
        database.Client.Ado.ExecuteCommand("UPDATE system_state SET revision_floor = 100 WHERE id = 1");
        var revision = versions.FreezeDraft(Document(), confirmed, "operator");
        Assert.Equal(101, revision);
        versions.Abandon(revision, "fixture");
        database.Client.Ado.ExecuteCommand("UPDATE system_state SET revision_floor = 9223372036854775807 WHERE id = 1");
        Assert.Throws<OverflowException>(() => versions.FreezeDraft(Document(), confirmed, "operator"));
        Assert.Null(versions.ReadPointers().PendingRevision);
        Assert.Single(database.Client.Queryable<RuntimeSnapshotRecord>().ToList());
    }
    [Fact]
    public async Task RunningMarkerCannotBeSetByPendingOrSupersededOperations()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var versions = factory.Services.GetRequiredService<RuntimeVersionStore>();
        var database = factory.Services.GetRequiredService<ConfigDatabase>();
        var confirmed = ReadDraftVersion(database);
        var old = versions.FreezeDraft(Document(), confirmed, "old");
        versions.Commit(old, null);
        versions.SetRunningMarker(old);
        var next = versions.FreezeDraft(Document(), confirmed, "next");
        Assert.Equal("runtime.effectiveMismatch", Assert.Throws<RuntimeVersionConflictException>(() => versions.SetRunningMarker(next)).Code);
        Assert.Equal(old, versions.ReadPointers().RunningRevision);
        versions.Commit(next, null);
        Assert.Throws<RuntimeVersionConflictException>(() => versions.SetRunningMarker(old));
        versions.SetRunningMarker(next);
        versions.ClearRunningMarker(old);
        Assert.Equal(next, versions.ReadPointers().RunningRevision);
        versions.ClearRunningMarker(next);
        Assert.Null(versions.ReadPointers().RunningRevision);
    }

}
