using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.Runtime.Deliveries;

namespace TenonEdge.IntegrationTests.Runtime;

public sealed class DeliveryManualRetryTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly IReadOnlyDictionary<long, string> Targets = new Dictionary<long, string> { [1] = "fp", [2] = "fp" };

    [Fact]
    public async Task RetryMovesOnlySelectedFailuresToTailAndPreservesAllFrozenIdentityAndContent()
    {
        using var data = TemporaryDirectory.Create();
        await using var runtime = RuntimeTesting.CreateRuntime([], data.Path);
        runtime.StartCore();
        var cache = runtime.Deliveries;
        var drafts = new[] { Draft(1), Draft(1), Draft(1), Draft(2) };
        var seqs = (await cache.EnqueueAsync(drafts, Token)).Seqs;
        var originals = cache.ReadPending(1, 0, 10);
        await cache.RecordFailureAsync(new(seqs[0], 10, "poison", true, true), Token);
        await cache.RecordFailureAsync(new(seqs[1], 2, "rejected", true, false), Token);
        await cache.RecordFailureAsync(new(seqs[3], 3, "other", true, false), Token);
        var result = await cache.RetryFailedAsync([Item(originals[1]), Item(originals[0])], Targets, Token);
        Assert.True(result.Committed);
        Assert.Equal(2, result.AffectedRecords);
        Assert.Empty(result.Seqs); // These are not new generated deliveries or collection activity.
        var pending = cache.ReadPending(1, 0, 10);
        Assert.Equal(drafts[2].DeliveryId, pending[0].DeliveryId);
        for (var i = 0; i < 2; i++)
        {
            var before = originals[i];
            var after = pending[i + 1];
            Assert.True(after.Seq > seqs.Max());
            Assert.Equal(before.DeliveryId, after.DeliveryId);
            Assert.Equal(before.SourceMessageId, after.SourceMessageId);
            Assert.Equal(before.ConnectionId, after.ConnectionId);
            Assert.Equal(before.DeviceKey, after.DeviceKey);
            Assert.Equal(before.RouteId, after.RouteId);
            Assert.Equal(before.NodeLabel, after.NodeLabel);
            Assert.Equal(before.Revision, after.Revision);
            Assert.Equal(before.Payload.ToArray(), after.Payload.ToArray());
            Assert.Equal(before.TargetAddress, after.TargetAddress);
            Assert.Equal(before.QualityOfService, after.QualityOfService);
            Assert.Equal(before.Headers, after.Headers);
            Assert.Equal(before.ProtocolMetadata, after.ProtocolMetadata);
            Assert.Equal(before.Fingerprint, after.Fingerprint);
            Assert.Equal(before.FingerprintSummary, after.FingerprintSummary);
            Assert.Equal(before.CreatedAt, after.CreatedAt);
            Assert.Equal(before.ByteSize, after.ByteSize);
            Assert.Equal(0, after.FailureCount);
            Assert.Null(after.FailedAt);
            Assert.Null(after.LastError);
            Assert.NotNull(after.LastRetriedAt);
        }
        Assert.True(pending[1].Seq < pending[2].Seq);
        Assert.Single(cache.ReadFailed(2, 0, 10));
        Assert.Equal(4, cache.GetPersistentCounters()["committedRecords"]);
        Assert.Equal(2, cache.GetPersistentCounters()["manuallyRetriedRecords"]);
        Assert.Equal(4, cache.GetStatus().CommittedRecords);
        var last = pending[2].Seq;
        await cache.DeleteAsync([last], Token);
        var next = Assert.Single((await cache.EnqueueAsync([Draft(1)], Token)).Seqs);
        Assert.True(next > last); // UPDATE of the primary key must also advance durable AUTOINCREMENT high water.
    }

    [Fact]
    public async Task ConcurrentIdenticalSelectionsRetryAtMostOnceAndCannotReplayAfterAnotherFailure()
    {
        using var data = TemporaryDirectory.Create();
        await using var runtime = RuntimeTesting.CreateRuntime([], data.Path);
        runtime.StartCore();
        var cache = runtime.Deliveries;
        var draft = Draft(1);
        var seq = Assert.Single((await cache.EnqueueAsync([draft], Token)).Seqs);
        await cache.RecordFailureAsync(new(seq, 10, "poison", true, true), Token);
        DeliveryRetryItem[] selected = [new(seq, draft.DeliveryId)];
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => cache.RetryFailedAsync(selected, Targets, Token)));
        Assert.Single(outcomes, outcome => outcome.Committed);
        Assert.All(outcomes.Where(outcome => !outcome.Committed), outcome => Assert.Equal(DeliveryRetryErrors.SelectionChanged, outcome.Error));
        var retried = Assert.Single(cache.ReadPending(1, 0, 10));
        await cache.RecordFailureAsync(new(retried.Seq, 1, "failed again", true, false), Token);
        Assert.Equal(DeliveryRetryErrors.SelectionChanged, (await cache.RetryFailedAsync(selected, Targets, Token)).Error);
        Assert.Equal(retried.Seq, Assert.Single(cache.ReadFailed(1, 0, 10)).Seq);
        Assert.Equal(1, cache.GetPersistentCounters()["manuallyRetriedRecords"]);
        await runtime.StopAsync(Token);
        await using var restarted = RuntimeTesting.CreateRuntime([], data.Path);
        restarted.StartCore();
        Assert.Equal(DeliveryRetryErrors.SelectionChanged, (await restarted.Deliveries.RetryFailedAsync(selected, Targets, Token)).Error);
        Assert.Equal(retried.Seq, Assert.Single(restarted.Deliveries.ReadFailed(1, 0, 10)).Seq);
    }

    [Theory]
    [InlineData("count")]
    [InlineData("bytes")]
    [InlineData("global")]
    public async Task AllTargetAndGlobalQuotaChecksRefuseTheWholeSelectionWithoutLossCounters(string constraint)
    {
        using var data = TemporaryDirectory.Create();
        var bytes = Draft(1).ByteSize;
        await using var runtime = RuntimeTesting.CreateRuntime([], data.Path, configureCache: options => new()
        {
            DatabasePath = options.DatabasePath,
            ConfigStoreId = options.ConfigStoreId,
            CommitInterval = options.CommitInterval,
            MaximumPendingPerTarget = constraint == "count" ? 1 : 100,
            MaximumPendingBytesPerTarget = constraint == "bytes" ? bytes : 100000,
            MaximumQueueBytes = constraint == "global" ? bytes * 2 : 100000,
        });
        runtime.StartCore();
        var cache = runtime.Deliveries;
        var drafts = new[] { Draft(1), Draft(2) };
        var seqs = (await cache.EnqueueAsync(drafts, Token)).Seqs;
        for (var i = 0; i < 2; i++)
        { await cache.RecordFailureAsync(new(seqs[i], 1, "rejected", true, false), Token); }
        await cache.EnqueueAsync([Draft(1)], Token);
        var result = await cache.RetryFailedAsync([new(seqs[0], drafts[0].DeliveryId), new(seqs[1], drafts[1].DeliveryId)], Targets, Token);
        Assert.Equal(DeliveryWriteStatus.Rejected, result.Status);
        Assert.Equal(DeliveryRetryErrors.QuotaExceeded, result.Error);
        Assert.Equal(0, result.AffectedRecords);
        Assert.Single(cache.ReadPending(1, 0, 10));
        Assert.Empty(cache.ReadPending(2, 0, 10));
        Assert.Single(cache.ReadFailed(1, 0, 10));
        Assert.Single(cache.ReadFailed(2, 0, 10));
        Assert.Equal(0, cache.GetQueueTotals(1).QuotaRejected);
        Assert.Equal(0, cache.GetPersistentCounters().GetValueOrDefault("manuallyRetriedRecords"));
        Assert.Equal(0, cache.GetStatus().RejectedRecords);
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("quarantined")]
    [InlineData("id")]
    [InlineData("fingerprint")]
    [InlineData("missingTarget")]
    public async Task SelectionOrTargetMismatchCannotPartiallyRetryOrBypassQuarantine(string mismatch)
    {
        using var data = TemporaryDirectory.Create();
        await using var runtime = RuntimeTesting.CreateRuntime([], data.Path);
        runtime.StartCore();
        var cache = runtime.Deliveries;
        var drafts = new[] { Draft(1), Draft(2) };
        var seqs = (await cache.EnqueueAsync(drafts, Token)).Seqs;
        for (var i = 0; i < 2; i++)
        { if (i == 0 || mismatch != "pending") { await cache.RecordFailureAsync(new(seqs[i], 1, "bad", true, false), Token); } }
        if (mismatch == "quarantined")
        { await cache.QuarantineAsync(seqs[1], DeliveryQuarantineReasons.TargetChanged, Token); }
        var targets = new Dictionary<long, string>(Targets);
        if (mismatch == "fingerprint")
        { targets[2] = "new-owner"; }
        if (mismatch == "missingTarget")
        { targets.Remove(2); }
        var result = await cache.RetryFailedAsync([new(seqs[0], drafts[0].DeliveryId), new(seqs[1], mismatch == "id" ? Guid.NewGuid() : drafts[1].DeliveryId)], targets, Token);
        Assert.Equal(DeliveryWriteStatus.Rejected, result.Status);
        Assert.Equal(mismatch is "fingerprint" or "missingTarget" ? DeliveryRetryErrors.TargetChanged : DeliveryRetryErrors.SelectionChanged, result.Error);
        Assert.Equal(seqs[0], Assert.Single(cache.ReadFailed(1, 0, 10)).Seq);
        Assert.Empty(cache.ReadPending(1, 0, 10));
    }

    [Fact]
    public async Task WriterRollsBackSequenceStateAndCounterTogetherAndCanRetryAfterStorageRecovers()
    {
        using var data = TemporaryDirectory.Create();
        await using var runtime = RuntimeTesting.CreateRuntime([], data.Path);
        runtime.StartCore();
        var cache = runtime.Deliveries;
        var drafts = new[] { Draft(1), Draft(1) };
        var seqs = (await cache.EnqueueAsync(drafts, Token)).Seqs;
        foreach (var seq in seqs)
        { await cache.RecordFailureAsync(new(seq, 10, "poison", true, true), Token); }
        var items = drafts.Select((draft, i) => new DeliveryRetryItem(seqs[i], draft.DeliveryId)).ToArray();
        Sql(data.Path, "CREATE TRIGGER fail_retry_counter BEFORE INSERT ON queue_counters WHEN NEW.name = 'manuallyRetriedRecords' BEGIN SELECT abs(-9223372036854775808); END;");
        Assert.Equal(DeliveryWriteStatus.StorageFailed, (await cache.RetryFailedAsync(items, Targets, Token)).Status);
        Assert.Equal(seqs, cache.ReadFailed(1, 0, 10).Select(record => record.Seq));
        Assert.Empty(cache.ReadPending(1, 0, 10));
        Assert.Equal(0, cache.GetPersistentCounters().GetValueOrDefault("manuallyRetriedRecords"));
        Sql(data.Path, "DROP TRIGGER fail_retry_counter;");
        Assert.True((await cache.RetryFailedAsync(items, Targets, Token)).Committed);
        Assert.Equal(new[] { seqs.Max() + 1, seqs.Max() + 2 }, cache.ReadPending(1, 0, 10).Select(record => record.Seq));
        Assert.Equal(2, cache.GetPersistentCounters()["manuallyRetriedRecords"]);
    }

    [Fact]
    public async Task RestartUsesNewRetentionBaselineWithoutDestroyingOriginalCreationTime()
    {
        using var data = TemporaryDirectory.Create();
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-13T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var created = time.GetUtcNow();
        var draft = Draft(1);
        await using (var runtime = RuntimeTesting.CreateRuntime([], data.Path, time: time, configureCache: options => new()
        { DatabasePath = options.DatabasePath, ConfigStoreId = options.ConfigStoreId, MaximumRecordsPerCommit = 1 }))
        {
            runtime.StartCore();
            var cache = runtime.Deliveries;
            var seq = Assert.Single((await cache.EnqueueAsync([draft], Token)).Seqs);
            await cache.RecordFailureAsync(new(seq, 10, "bad", true, true), Token);
            time.Advance(TimeSpan.FromDays(2));
            Assert.True((await cache.RetryFailedAsync([new(seq, draft.DeliveryId)], Targets, Token)).Committed);
            await runtime.StopAsync(Token);
        }
        var retriedAt = time.GetUtcNow();
        var reopened = RuntimeTesting.CreateDeliveryCache(data.Path, time);
        using var writer = reopened.Store.OpenWriteConnection();
        try
        {
            reopened.Store.ExpirePending(writer);
            var record = Assert.Single(reopened.ReadPending(1, 0, 10));
            Assert.Equal(created, record.CreatedAt);
            Assert.Equal(retriedAt, record.LastRetriedAt);
            time.Advance(TimeSpan.FromHours(24) - TimeSpan.FromMilliseconds(1));
            reopened.Store.ExpirePending(writer);
            Assert.Single(reopened.ReadPending(1, 0, 10));
            time.Advance(TimeSpan.FromMilliseconds(1));
            reopened.Store.ExpirePending(writer);
            Assert.Empty(reopened.ReadPending(1, 0, 10));
            Assert.Equal(1, reopened.GetQueueTotals(1).Expired);
            reopened.Store.ExpirePending(writer);
            Assert.Equal(1, reopened.GetQueueTotals(1).Expired);
        }
        finally { reopened.Store.ClearReadPool(); }
    }

    [Fact]
    public async Task VersionFiveMigrationDoesNotInventARetryOrExtendOldRetention()
    {
        using var data = TemporaryDirectory.Create();
        await using (var runtime = RuntimeTesting.CreateRuntime([], data.Path))
        {
            runtime.StartCore();
            await runtime.Deliveries.EnqueueAsync([Draft(1)], Token);
            await runtime.StopAsync(Token);
        }
        Sql(data.Path, "DROP INDEX ix_delivery_migration_intent; ALTER TABLE delivery DROP COLUMN migration_intent_id; ALTER TABLE delivery DROP COLUMN migration_previous_state; DROP TABLE delivery_migration_hold; DROP TABLE delivery_migration_receipt; ALTER TABLE delivery DROP COLUMN last_retried_at; PRAGMA user_version=5;");
        var reopened = RuntimeTesting.CreateDeliveryCache(data.Path);
        try
        {
            var record = Assert.Single(reopened.ReadPending(1, 0, 10));
            Assert.Null(record.LastRetriedAt);
            Assert.Equal(8, reopened.Store.Info!.SchemaVersion);
            Assert.Equal("{\"frozen\":true}", record.ProtocolMetadata);
        }
        finally { reopened.Store.ClearReadPool(); }
    }

    [Fact]
    public async Task StorageProtectionRefusesRetryWithoutDeletingFailures()
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
        var result = await runtime.Deliveries.RetryFailedAsync([new(seq, draft.DeliveryId)], Targets, Token);
        Assert.Equal(DeliveryRetryErrors.StorageProtection, result.Error);
        Assert.Equal(DeliveryWriteStatus.Rejected, result.Status);
        Assert.Equal(seq, Assert.Single(runtime.Deliveries.ReadFailed(1, 0, 10)).Seq);
    }

    [Fact]
    public async Task SequenceExhaustionDoesNotOverflowOrReuseDeletedSequenceNumbers()
    {
        using var data = TemporaryDirectory.Create();
        await using var runtime = RuntimeTesting.CreateRuntime([], data.Path);
        runtime.StartCore();
        var draft = Draft(1);
        var seq = Assert.Single((await runtime.Deliveries.EnqueueAsync([draft], Token)).Seqs);
        await runtime.Deliveries.RecordFailureAsync(new(seq, 1, "bad", true, false), Token);
        Sql(data.Path, "UPDATE sqlite_sequence SET seq = 9223372036854775807 WHERE name = 'delivery';");
        Assert.Equal(DeliveryRetryErrors.SequenceExhausted,
            (await runtime.Deliveries.RetryFailedAsync([new(seq, draft.DeliveryId)], Targets, Token)).Error);
        Assert.Equal(seq, Assert.Single(runtime.Deliveries.ReadFailed(1, 0, 10)).Seq);
    }

    [Fact]
    public async Task MaximumSelectionIsOneAtomicOrderedRetryAndNotNewCollectionActivity()
    {
        using var data = TemporaryDirectory.Create();
        await using var runtime = RuntimeTesting.CreateRuntime([], data.Path, configureCache: options => new()
        { DatabasePath = options.DatabasePath, ConfigStoreId = options.ConfigStoreId, CommitInterval = options.CommitInterval, MaximumFailedPerTarget = 1000 });
        runtime.StartCore();
        var cache = runtime.Deliveries;
        var drafts = Enumerable.Range(0, 1000).Select(_ => Draft(1)).ToArray();
        var seqs = (await cache.EnqueueAsync(drafts, Token)).Seqs;
        var failures = await Task.WhenAll(seqs.Select(seq => cache.RecordFailureAsync(new(seq, 10, "bad", true, true), Token)));
        Assert.All(failures, result => Assert.True(result.Committed));
        var selected = drafts.Select((draft, i) => new DeliveryRetryItem(seqs[i], draft.DeliveryId)).Reverse().ToArray();
        var result = await cache.RetryFailedAsync(selected, Targets, Token);
        Assert.True(result.Committed);
        Assert.Equal(1000, result.AffectedRecords);
        Assert.Equal(drafts.Select(draft => draft.DeliveryId), cache.ReadPending(1, 0, 1000).Select(record => record.DeliveryId));
        Assert.Equal(1000, cache.GetPersistentCounters()["committedRecords"]);
        Assert.Equal(1000, cache.GetPersistentCounters()["manuallyRetriedRecords"]);
    }

    [Fact]
    public async Task CancelingCallerWaitDoesNotWithdrawQueuedRetryOrPermitAnOldSelectionReplay()
    {
        using var data = TemporaryDirectory.Create();
        await using var runtime = RuntimeTesting.CreateRuntime([], data.Path, configureCache: options => new()
        { DatabasePath = options.DatabasePath, ConfigStoreId = options.ConfigStoreId, CommitInterval = TimeSpan.FromMilliseconds(200) });
        runtime.StartCore();
        var cache = runtime.Deliveries;
        var draft = Draft(1);
        var seq = Assert.Single((await cache.EnqueueAsync([draft], Token)).Seqs);
        await cache.RecordFailureAsync(new(seq, 1, "bad", true, false), Token);
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var request = cache.RetryFailedAsync([new(seq, draft.DeliveryId)], Targets, waiting.Token);
        await waiting.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        await OutputPipelineTests.UntilAsync(() => cache.ReadPending(1, 0, 10).Count == 1);
        Assert.True(Assert.Single(cache.ReadPending(1, 0, 10)).Seq > seq);
        Assert.Equal(DeliveryRetryErrors.SelectionChanged, (await cache.RetryFailedAsync([new(seq, draft.DeliveryId)], Targets, Token)).Error);
        Assert.Equal(1, cache.GetPersistentCounters()["manuallyRetriedRecords"]);
    }

    private static DeliveryRetryItem Item(StoredDelivery delivery) => new(delivery.Seq, delivery.DeliveryId);
    private static DeliveryDraft Draft(long id) => new(Guid.NewGuid(), id, Guid.NewGuid(), "meter", 17, "frozen-payload"u8.ToArray(), "fp", "same-owner", "frozen-topic", 1,
        new Dictionary<string, string> { ["X-NonSecret"] = "frozen" }, routeId: 41, nodeLabel: "out", protocolMetadata: "{\"frozen\":true}");
    private static void Sql(string directory, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(directory, "queue.db")};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
