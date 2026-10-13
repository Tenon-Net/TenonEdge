using Microsoft.Data.Sqlite;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.Runtime.Deliveries;

namespace TenonEdge.IntegrationTests.Runtime;

public sealed class DeliveryCleanupTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RemovesOnlySelectedAreaAndGenerationWithDurableIndependentLossCounts()
    {
        using var data = TemporaryDirectory.Create();
        DeliveryCleanupSelection selection;
        await using (var runtime = RuntimeTesting.CreateRuntime([], data.Path))
        {
            runtime.StartCore();
            var cache = runtime.Deliveries;
            var drafts = new[] { Draft(1), Draft(2), Draft(1), Draft(1) };
            var seqs = (await cache.EnqueueAsync(drafts, Token)).Seqs;
            await cache.RecordFailureAsync(new(seqs[0], 10, "bad", true, true), Token);
            await cache.RecordFailureAsync(new(seqs[1], 1, "bad", true, false), Token);
            await cache.QuarantineAsync(seqs[3], DeliveryQuarantineReasons.TargetMissing, Token);
            selection = new(DeliveryState.Failed, [Item(seqs[0], drafts[0]), Item(seqs[1], drafts[1])]);
            var newDraft = Draft(1);
            var added = Assert.Single((await cache.EnqueueAsync([newDraft], Token)).Seqs);
            await cache.RecordFailureAsync(new(added, 1, "same reason after review", true, false), Token);
            var result = await cache.CleanupAsync(selection, Token);
            Assert.True(result.Committed);
            Assert.Equal(2, result.AffectedRecords);
            Assert.Empty(result.Seqs);
            Assert.Equal(newDraft.DeliveryId, Assert.Single(cache.ReadFailed(1, 0, 10)).DeliveryId);
            Assert.Empty(cache.ReadFailed(2, 0, 10));
            Assert.Equal(drafts[2].DeliveryId, Assert.Single(cache.ReadPending(1, 0, 10)).DeliveryId);
            Assert.Single(cache.ReadQuarantined(1, 0, 10));
            Assert.True((await cache.CleanupAsync(new(DeliveryState.Quarantined, [Item(seqs[3], drafts[3])]), Token)).Committed);
            Assert.Empty(cache.ReadQuarantined(1, 0, 10));
            Assert.Equal(0, cache.GetStatus().DeletedRecords); // Sender acknowledgements, not administrative loss.
            Assert.Equal(5, cache.GetStatus().CommittedRecords);
            Assert.Equal(0, cache.GetPersistentCounters().GetValueOrDefault("acknowledgedRecords"));
            Assert.Equal(1, cache.GetPersistentCounters()["target/2/manuallyClearedFailedRecords"]);
            await runtime.StopAsync(Token);
        }
        await using var reopened = RuntimeTesting.CreateRuntime([], data.Path);
        reopened.StartCore();
        Assert.Equal(2, reopened.Deliveries.GetPersistentCounters()["manuallyClearedFailedRecords"]);
        Assert.Equal(1, reopened.Deliveries.GetPersistentCounters()["manuallyClearedQuarantinedRecords"]);
        Assert.Equal(DeliveryCleanupErrors.SelectionChanged, (await reopened.Deliveries.CleanupAsync(selection, Token)).Error);
        Assert.Equal(2, reopened.Deliveries.GetPersistentCounters()["manuallyClearedFailedRecords"]);
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("quarantined")]
    [InlineData("id")]
    [InlineData("connection")]
    [InlineData("migration")]
    [InlineData("retried")]
    public async Task OneStaleSelectionRejectsTheWholeBatch(string changed)
    {
        using var data = TemporaryDirectory.Create();
        await using var runtime = RuntimeTesting.CreateRuntime([], data.Path);
        runtime.StartCore();
        var cache = runtime.Deliveries;
        var drafts = new[] { Draft(1), Draft(1) };
        var seqs = (await cache.EnqueueAsync(drafts, Token)).Seqs;
        await cache.RecordFailureAsync(new(seqs[0], 1, "bad", true, false), Token);
        if (changed != "pending")
        { await cache.RecordFailureAsync(new(seqs[1], 1, "bad", true, false), Token); }
        var items = new[] { Item(seqs[0], drafts[0]), Item(seqs[1], drafts[1]) };
        if (changed == "quarantined")
        { await cache.QuarantineAsync(seqs[1], DeliveryQuarantineReasons.TargetChanged, Token); }
        if (changed == "id")
        { items[1] = items[1] with { DeliveryId = Guid.NewGuid() }; }
        if (changed == "connection")
        { items[1] = items[1] with { ConnectionId = 2 }; }
        if (changed == "migration")
        {
            // Only a migration-generation fixture, not acceptance of the not-yet-implemented migration operation.
            Sql(data.Path, "UPDATE delivery SET migration_id = 9223372036854775807 WHERE seq = " + seqs[1]);
            Assert.Equal(long.MaxValue, cache.Inspect(new(DeliveryState.Failed)).Items.Single(item => item.Sequence == seqs[1]).MigrationId);
        }
        if (changed == "retried")
        {
            await cache.RetryFailedAsync([new(seqs[1], drafts[1].DeliveryId)], new Dictionary<long, string> { [1] = "fp" }, Token);
            await cache.RecordFailureAsync(new(Assert.Single(cache.ReadPending(1, 0, 10)).Seq, 1, "bad again", true, false), Token);
        }
        Assert.Equal(DeliveryCleanupErrors.SelectionChanged, (await cache.CleanupAsync(new(DeliveryState.Failed, items), Token)).Error);
        Assert.Contains(cache.ReadFailed(1, 0, 10), item => item.DeliveryId == drafts[0].DeliveryId);
        Assert.Equal(0, cache.GetPersistentCounters().GetValueOrDefault("manuallyClearedFailedRecords"));
    }

    [Fact]
    public async Task ConcurrentReplaysCommitOnlyOnceAndNeverReuseDeletedSequence()
    {
        using var data = TemporaryDirectory.Create();
        await using var runtime = RuntimeTesting.CreateRuntime([], data.Path);
        runtime.StartCore();
        var cache = runtime.Deliveries;
        var draft = Draft(1);
        var seq = Assert.Single((await cache.EnqueueAsync([draft], Token)).Seqs);
        await cache.QuarantineAsync(seq, DeliveryQuarantineReasons.ConfigStoreChanged, Token);
        var selection = new DeliveryCleanupSelection(DeliveryState.Quarantined, [Item(seq, draft)]);
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => cache.CleanupAsync(selection, Token)));
        Assert.Single(results, result => result.Committed);
        Assert.Equal(7, results.Count(result => result.Error == DeliveryCleanupErrors.SelectionChanged));
        Assert.Equal(1, cache.GetPersistentCounters()["manuallyClearedQuarantinedRecords"]);
        Assert.True(Assert.Single((await cache.EnqueueAsync([Draft(1)], Token)).Seqs) > seq);
    }

    [Fact]
    public async Task CounterFailureRollsBackDeletesAndCountsThenAllowsRecovery()
    {
        using var data = TemporaryDirectory.Create();
        await using var runtime = RuntimeTesting.CreateRuntime([], data.Path);
        runtime.StartCore();
        var cache = runtime.Deliveries;
        var drafts = new[] { Draft(1), Draft(2) };
        var seqs = (await cache.EnqueueAsync(drafts, Token)).Seqs;
        foreach (var seq in seqs)
        { await cache.RecordFailureAsync(new(seq, 1, "bad", true, false), Token); }
        var selection = new DeliveryCleanupSelection(DeliveryState.Failed, drafts.Select((draft, i) => Item(seqs[i], draft)).ToArray());
        Sql(data.Path, "CREATE TRIGGER fail_cleanup_counter BEFORE INSERT ON queue_counters WHEN NEW.name = 'target/2/manuallyClearedFailedRecords' BEGIN SELECT abs(-9223372036854775808); END;");
        Assert.Equal(DeliveryWriteStatus.StorageFailed, (await cache.CleanupAsync(selection, Token)).Status);
        Assert.Single(cache.ReadFailed(1, 0, 10));
        Assert.Single(cache.ReadFailed(2, 0, 10));
        Assert.Equal(0, cache.GetPersistentCounters().GetValueOrDefault("manuallyClearedFailedRecords"));
        Assert.Equal(0, cache.GetPersistentCounters().GetValueOrDefault("target/1/manuallyClearedFailedRecords"));
        Sql(data.Path, "DROP TRIGGER fail_cleanup_counter;");
        Assert.Equal(2, (await cache.CleanupAsync(selection, Token)).AffectedRecords);
        Assert.Empty(cache.ReadFailed(1, 0, 10));
        Assert.Empty(cache.ReadFailed(2, 0, 10));
    }

    [Fact]
    public async Task CleanupIsAvailableUnderDiskStorageProtectionAndDoesNotAcknowledgeOrSend()
    {
        using var data = TemporaryDirectory.Create();
        var draft = Draft(1);
        long seq;
        await using (var first = RuntimeTesting.CreateRuntime([], data.Path))
        {
            first.StartCore();
            seq = Assert.Single((await first.Deliveries.EnqueueAsync([draft], Token)).Seqs);
            await first.Deliveries.RecordFailureAsync(new(seq, 1, "bad", true, false), Token);
            await first.StopAsync(Token);
        }
        await using var runtime = RuntimeTesting.CreateRuntime([], data.Path, configureCache: options => new()
        { DatabasePath = options.DatabasePath, ConfigStoreId = options.ConfigStoreId, MinimumDiskFreeBytes = long.MaxValue });
        runtime.StartCore();
        Assert.True(runtime.Deliveries.StorageProtection.Status.RejectNewRecords);
        Assert.True((await runtime.Deliveries.CleanupAsync(new(DeliveryState.Failed, [Item(seq, draft)]), Token)).Committed);
        Assert.Empty(runtime.Deliveries.ReadFailed(1, 0, 10));
        Assert.Equal(0, runtime.Deliveries.GetPersistentCounters().GetValueOrDefault("acknowledgedRecords"));
    }

    [Fact]
    public async Task MaximumBatchHasNoHiddenPartialCommits()
    {
        using var data = TemporaryDirectory.Create();
        await using var runtime = RuntimeTesting.CreateRuntime([], data.Path);
        runtime.StartCore();
        var cache = runtime.Deliveries;
        var drafts = Enumerable.Range(0, 1000).Select(_ => Draft(1)).ToArray();
        var seqs = (await cache.EnqueueAsync(drafts, Token)).Seqs;
        await cache.ReconcileAsync(new Dictionary<long, string>(), true, Token);
        var result = await cache.CleanupAsync(new(DeliveryState.Quarantined, drafts.Select((draft, i) => Item(seqs[i], draft)).ToArray()), Token);
        Assert.True(result.Committed);
        Assert.Equal(1000, result.AffectedRecords);
        Assert.Empty(cache.ReadQuarantined(1, 0, 1000));
        Assert.Equal(1000, cache.GetPersistentCounters()["manuallyClearedQuarantinedRecords"]);
    }

    private static DeliveryDraft Draft(long target) => new(Guid.NewGuid(), target, Guid.NewGuid(), "meter", 1, "private-frozen-body"u8.ToArray(), "fp", "owner", "send");
    private static DeliveryCleanupItem Item(long seq, DeliveryDraft draft) => new(seq, draft.DeliveryId, draft.ConnectionId, null);
    private static void Sql(string directory, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(directory, "queue.db")};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
