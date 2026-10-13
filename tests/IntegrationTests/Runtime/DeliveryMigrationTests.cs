using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.Runtime.Deliveries;

namespace TenonEdge.IntegrationTests.Runtime;

public sealed class DeliveryMigrationTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private const string NextFingerprint = "next-fingerprint";
    private const string NextSummary = "新接收方：同一所有者";

    [Fact]
    public async Task SelectedMigrationPreservesFrozenContentSequenceAndCurrentFailedState()
    {
        using var data = TemporaryDirectory.Create();
        await using var runtime = RuntimeTesting.CreateRuntime([], data.Path);
        runtime.StartCore();
        var cache = runtime.Deliveries;
        var drafts = Enumerable.Range(0, 5).Select(_ => Draft(1)).ToArray();
        var seqs = (await cache.EnqueueAsync(drafts, Token)).Seqs;
        var selected = cache.ReadPending(1, 0, 10).Select(Item).ToArray();
        await cache.RecordFailureAsync(new(seqs[0], 4, "retry", false, false), Token);
        await cache.RecordFailureAsync(new(seqs[1], 2, "bad payload", true, false), Token);
        await cache.QuarantineAsync(seqs[2], DeliveryQuarantineReasons.TargetChanged, Token);
        await cache.QuarantineAsync(seqs[3], DeliveryQuarantineReasons.TargetMissing, Token);
        await cache.RecordFailureAsync(new(seqs[4], 10, "poison", true, true), Token);
        await cache.QuarantineAsync(seqs[4], DeliveryQuarantineReasons.ConfigStoreChanged, Token);
        var originals = All(cache, 1).ToArray();
        var command = Selected(selected);
        var result = await cache.MigrateAsync(command);
        Assert.True(result.Committed, result.Error);
        Assert.Equal(5, result.AffectedRecords);
        Assert.Empty(result.Seqs);
        var receipt = Assert.IsType<DeliveryMigrationReceipt>(result.Migration);
        Assert.Equal(receipt, cache.ReadMigrationReceipt(command.OperationId));
        var records = All(cache, 2).ToArray();
        Assert.Empty(All(cache, 1));
        for (var i = 0; i < records.Length; i++)
        {
            var before = originals[i];
            var after = records[i];
            AssertFrozen(before, after);
            Assert.Equal(2, after.ConnectionId);
            Assert.Equal(NextFingerprint, after.Fingerprint);
            Assert.Equal(NextSummary, after.FingerprintSummary);
            Assert.Equal(receipt.MigrationId, after.MigrationId);
            Assert.Equal(0, after.FailureCount);
            Assert.Null(after.QuarantineReason);
            Assert.Null(after.LastRetriedAt);
            Assert.Equal(before.LastError, after.LastError);
            Assert.Equal(before.State == DeliveryState.Failed ? DeliveryState.Failed : DeliveryState.Pending, after.State);
            Assert.Equal(before.State == DeliveryState.Failed ? before.FailedAt : null, after.FailedAt);
            var expectedBytes = before.ByteSize - Encoding.UTF8.GetByteCount(before.Fingerprint) - Encoding.UTF8.GetByteCount(before.FingerprintSummary)
                + Encoding.UTF8.GetByteCount(NextFingerprint) + Encoding.UTF8.GetByteCount(NextSummary);
            Assert.Equal(expectedBytes, after.ByteSize);
        }
        Assert.Equal(5, cache.GetPersistentCounters()["manuallyMigratedRecords"]);
        Assert.Equal(5, cache.GetStatus().CommittedRecords);
        Assert.Equal(0, cache.GetStatus().DeletedRecords);
        Assert.Equal(0, cache.GetPersistentCounters().GetValueOrDefault("acknowledgedRecords"));
        Assert.Equal(5, cache.GetPersistentCounters()["committedRecords"]);
        await runtime.StopAsync(Token);
        await using var restarted = RuntimeTesting.CreateRuntime([], data.Path);
        restarted.StartCore();
        Assert.Equal(receipt, restarted.Deliveries.ReadMigrationReceipt(command.OperationId));
        Assert.Equal(receipt, (await restarted.Deliveries.MigrateAsync(command)).Migration);
        Assert.Equal(5, restarted.Deliveries.GetPersistentCounters()["manuallyMigratedRecords"]);
    }

    [Fact]
    public async Task OldFingerprintRangeIsCompleteBeyondOnePageAndExcludesOtherQuarantineReasons()
    {
        using var data = TemporaryDirectory.Create();
        await using var runtime = RuntimeTesting.CreateRuntime([], data.Path);
        runtime.StartCore();
        var cache = runtime.Deliveries;
        var drafts = Enumerable.Range(0, 1003).Select(_ => Draft(1)).Concat(
            [Draft(1), Draft(1), Draft(1), Draft(1), Draft(1, "other"), Draft(2)]).ToArray();
        var seqs = (await cache.EnqueueAsync(drafts, Token)).Seqs;
        Assert.Equal(1009, seqs.Count);
        await cache.RecordFailureAsync(new(seqs[1003], 3, "bad", true, false), Token);
        await cache.QuarantineAsync(seqs[1004], DeliveryQuarantineReasons.TargetChanged, Token);
        await cache.QuarantineAsync(seqs[1005], DeliveryQuarantineReasons.TargetMissing, Token);
        await cache.QuarantineAsync(seqs[1006], DeliveryQuarantineReasons.ConfigStoreChanged, Token);
        var command = DeliveryMigrationCommand.OldTarget(RuntimeTesting.ConfigStoreId, Guid.NewGuid(), 1, "fp", NextFingerprint, NextSummary);
        var result = await cache.MigrateAsync(command);
        Assert.True(result.Committed, result.Error);
        Assert.Equal(1005, result.AffectedRecords);
        Assert.Equal(1005L, Scalar(data.Path, "SELECT COUNT(*) FROM delivery WHERE migration_id IS NOT NULL"));
        Assert.Equal(1004L, Scalar(data.Path, "SELECT COUNT(*) FROM delivery WHERE migration_id IS NOT NULL AND state='pending'"));
        Assert.Equal(seqs[1003], Assert.Single(cache.ReadFailed(1, 0, 10)).Seq);
        Assert.Equal(2, cache.ReadQuarantined(1, 0, 10).Count);
        Assert.Single(cache.ReadPending(2, 0, 10));
        // 重放只能返回原收据，不能把之后新到的旧指纹记录也纳入。
        await cache.EnqueueAsync([Draft(1)], Token);
        var replay = await cache.MigrateAsync(command);
        Assert.Equal(result.Migration, replay.Migration);
        Assert.Equal(1L, Scalar(data.Path, "SELECT COUNT(*) FROM delivery WHERE connection_id=1 AND fingerprint='fp' AND state='pending'"));
        Assert.Equal(1005, cache.GetPersistentCounters()["manuallyMigratedRecords"]);
    }

    [Fact]
    public async Task ConcurrentReplayAndChangedCommandCannotDuplicateMigrationOrMoveAdditionalRecords()
    {
        using var data = TemporaryDirectory.Create();
        await using var runtime = RuntimeTesting.CreateRuntime([], data.Path);
        runtime.StartCore();
        var cache = runtime.Deliveries;
        await cache.EnqueueAsync([Draft(1), Draft(1)], Token);
        var records = cache.ReadPending(1, 0, 10);
        var command = Selected(records.Select(Item).ToArray());
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => cache.MigrateAsync(command)));
        Assert.All(results, result => Assert.True(result.Committed, result.Error));
        Assert.All(results, result => Assert.Equal(results[0].Migration, result.Migration));
        Assert.Equal(1L, Scalar(data.Path, "SELECT COUNT(*) FROM delivery_migration_receipt"));
        Assert.Equal(2, cache.GetPersistentCounters()["manuallyMigratedRecords"]);
        var changed = DeliveryMigrationCommand.Selected(RuntimeTesting.ConfigStoreId, command.OperationId, 1, 3,
            NextFingerprint, NextSummary, records.Select(Item).ToArray());
        Assert.Equal(DeliveryMigrationErrors.OperationChanged, (await cache.MigrateAsync(changed)).Error);
        Assert.Equal(2, cache.ReadPending(2, 0, 10).Count);
        Assert.Empty(cache.ReadPending(3, 0, 10));
    }

    [Theory]
    [InlineData("count")]
    [InlineData("bytes")]
    [InlineData("global")]
    [InlineData("failed")]
    [InlineData("failedPendingCount")]
    [InlineData("failedPendingBytes")]
    public async Task EveryQuotaRefusesTheEntireSelectionWithStructuredExactDeficits(string constraint)
    {
        using var data = TemporaryDirectory.Create();
        var bytes = Draft(1).ByteSize;
        var delta = Encoding.UTF8.GetByteCount(NextFingerprint) + Encoding.UTF8.GetByteCount(NextSummary) - 2 - 5;
        await using var runtime = RuntimeTesting.CreateRuntime([], data.Path, configureCache: options => new()
        {
            DatabasePath = options.DatabasePath,
            ConfigStoreId = options.ConfigStoreId,
            CommitInterval = options.CommitInterval,
            MaximumPendingPerTarget = constraint is "count" or "failedPendingCount" ? 2 : 100,
            MaximumPendingBytesPerTarget = constraint is "bytes" or "failedPendingBytes" ? 2 * bytes : 1000000,
            MaximumQueueBytes = constraint == "global" ? 3 * bytes : 1000000,
            MaximumFailedPerTarget = constraint == "failed" ? 1 : 100,
        });
        runtime.StartCore();
        var cache = runtime.Deliveries;
        var seqs = (await cache.EnqueueAsync([Draft(1), Draft(1), Draft(2)], Token)).Seqs;
        if (constraint is "failed" or "failedPendingCount" or "failedPendingBytes")
        { await cache.RecordFailureAsync(new(seqs[0], 1, "bad", true, false), Token); }
        if (constraint == "failed")
        { await cache.RecordFailureAsync(new(seqs[2], 1, "bad", true, false), Token); }
        var originals = All(cache, 1).ToArray();
        var command = Selected(originals.Select(Item).ToArray());
        var result = await cache.MigrateAsync(command);
        Assert.Equal(DeliveryWriteStatus.Rejected, result.Status);
        Assert.Equal(DeliveryMigrationErrors.QuotaExceeded, result.Error);
        var deficit = Assert.IsType<DeliveryMigrationQuotaDeficit>(result.MigrationQuota);
        Assert.Equal(constraint is "count" or "failedPendingCount" ? 1 : 0, deficit.PendingRecords);
        Assert.Equal(constraint is "bytes" or "failedPendingBytes" ? bytes + 2 * delta : 0, deficit.PendingBytes);
        Assert.Equal(constraint == "global" ? 2 * delta : 0, deficit.GlobalBytes);
        Assert.Equal(constraint == "failed" ? 1 : 0, deficit.FailedRecords);
        Assert.Null(cache.ReadMigrationReceipt(command.OperationId));
        Assert.Equal(originals.Select(record => (record.Seq, record.State, record.Fingerprint, record.MigrationId)),
            All(cache, 1).Select(record => (record.Seq, record.State, record.Fingerprint, record.MigrationId)));
        Assert.Equal(0, cache.GetQueueTotals(2).QuotaRejected);
        Assert.Equal(0, cache.GetPersistentCounters().GetValueOrDefault("manuallyMigratedRecords"));
        Assert.Equal(0, cache.GetStatus().RejectedRecords);
    }

    [Fact]
    public async Task InPlaceMigrationDoesNotDoubleCountExistingPendingQuotaOrChangeSequenceHighWater()
    {
        using var data = TemporaryDirectory.Create();
        var bytes = Draft(1).ByteSize;
        await using var runtime = RuntimeTesting.CreateRuntime([], data.Path, configureCache: options => new()
        {
            DatabasePath = options.DatabasePath,
            ConfigStoreId = options.ConfigStoreId,
            CommitInterval = options.CommitInterval,
            MaximumPendingPerTarget = 2,
            MaximumPendingBytesPerTarget = 2 * bytes,
            MaximumQueueBytes = 2 * bytes,
        });
        runtime.StartCore();
        var cache = runtime.Deliveries;
        var seqs = (await cache.EnqueueAsync([Draft(1), Draft(1)], Token)).Seqs;
        var result = await cache.MigrateAsync(DeliveryMigrationCommand.OldTarget(RuntimeTesting.ConfigStoreId, Guid.NewGuid(), 1, "fp", "np", "owner"));
        Assert.True(result.Committed, result.Error);
        Assert.Equal(seqs, cache.ReadPending(1, 0, 10).Select(record => record.Seq));
        Assert.Equal(2 * bytes, cache.GetQueueTotals(1).PendingBytes);
        Assert.Equal(seqs.Max(), Scalar(data.Path, "SELECT seq FROM sqlite_sequence WHERE name='delivery'"));
    }

    [Fact]
    public async Task FailedRecordsRemainOutsideGlobalPendingQuotaAndKeepTheirOriginalFailureTimestamp()
    {
        using var data = TemporaryDirectory.Create();
        var bytes = Draft(1).ByteSize;
        await using var runtime = RuntimeTesting.CreateRuntime([], data.Path, configureCache: options => new()
        {
            DatabasePath = options.DatabasePath,
            ConfigStoreId = options.ConfigStoreId,
            CommitInterval = options.CommitInterval,
            MaximumQueueBytes = bytes,
        });
        runtime.StartCore();
        var cache = runtime.Deliveries;
        var seq = Assert.Single((await cache.EnqueueAsync([Draft(1)], Token)).Seqs);
        await cache.RecordFailureAsync(new(seq, 1, "bad", true, false), Token);
        var before = Assert.Single(cache.ReadFailed(1, 0, 10));
        await cache.EnqueueAsync([Draft(3)], Token); // Global pending quota is exactly full.
        var result = await cache.MigrateAsync(Selected([Item(before)]));
        Assert.True(result.Committed, result.Error);
        var after = Assert.Single(cache.ReadFailed(2, 0, 10));
        Assert.Equal(before.FailedAt, after.FailedAt);
        AssertFrozen(before, after);
        Assert.Single(cache.ReadPending(3, 0, 10));
        Assert.Equal(0, cache.GetQueueTotals(2).PendingCount);
    }

    [Theory]
    [InlineData("sequence")]
    [InlineData("stableId")]
    [InlineData("fingerprint")]
    [InlineData("connection")]
    [InlineData("migration")]
    [InlineData("manualRetry")]
    public async Task ExistingButChangedRecordIsNotMistakenForAcknowledgedAndRejectsWholeSelection(string change)
    {
        using var data = TemporaryDirectory.Create();
        await using var runtime = RuntimeTesting.CreateRuntime([], data.Path);
        runtime.StartCore();
        var cache = runtime.Deliveries;
        var seqs = (await cache.EnqueueAsync([Draft(1), Draft(1)], Token)).Seqs;
        var records = cache.ReadPending(1, 0, 10);
        var items = records.Select(Item).ToArray();
        switch (change)
        {
            case "sequence":
                items[1] = items[1] with { Sequence = 99 };
                break;
            case "stableId":
                items[1] = items[1] with { DeliveryId = Guid.NewGuid() };
                break;
            case "fingerprint":
                items[1] = items[1] with { Fingerprint = "different" };
                break;
            case "connection":
                Sql(data.Path, "UPDATE delivery SET connection_id=3 WHERE seq=2");
                break;
            case "migration":
                var intermediate = DeliveryMigrationCommand.Selected(RuntimeTesting.ConfigStoreId, Guid.NewGuid(), 1, 1, "fp", "owner", [items[1]]);
                Assert.True((await cache.MigrateAsync(intermediate)).Committed);
                break;
            case "manualRetry":
                await cache.RecordFailureAsync(new(seqs[1], 1, "bad", true, false), Token);
                Assert.True((await cache.RetryFailedAsync([new(seqs[1], records[1].DeliveryId)], new Dictionary<long, string> { [1] = "fp" }, Token)).Committed);
                break;
        }
        var command = Selected(items);
        var result = await cache.MigrateAsync(command);
        Assert.Equal(DeliveryMigrationErrors.SelectionChanged, result.Error);
        Assert.Equal(DeliveryWriteStatus.Rejected, result.Status);
        Assert.Null(cache.ReadMigrationReceipt(command.OperationId));
        Assert.Contains(cache.ReadPending(1, 0, 10), record => record.Seq == seqs[0] && record.Fingerprint == "fp" && record.MigrationId is null);
        Assert.Empty(cache.ReadPending(2, 0, 10));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcknowledgedRecordsAreExcludedAndZeroActualCountIsDurableAndIdempotent(bool deleteAll)
    {
        using var data = TemporaryDirectory.Create();
        await using var runtime = RuntimeTesting.CreateRuntime([], data.Path);
        runtime.StartCore();
        var cache = runtime.Deliveries;
        await cache.EnqueueAsync([Draft(1), Draft(1)], Token);
        var originals = cache.ReadPending(1, 0, 10);
        var command = Selected(originals.Select(Item).ToArray());
        await cache.DeleteAsync(deleteAll ? originals.Select(record => record.Seq).ToArray() : [originals[0].Seq], Token);
        var result = await cache.MigrateAsync(command);
        Assert.True(result.Committed, result.Error);
        Assert.Equal(deleteAll ? 0 : 1, result.AffectedRecords);
        Assert.Equal(result.Migration, cache.ReadMigrationReceipt(command.OperationId));
        await cache.EnqueueAsync([Draft(1)], Token);
        Assert.Equal(result.Migration, (await cache.MigrateAsync(command)).Migration);
        Assert.Single(cache.ReadPending(1, 0, 10));
        Assert.Equal(deleteAll ? 2 : 1, cache.GetPersistentCounters()["acknowledgedRecords"]);
        Assert.Equal(deleteAll ? 0 : 1, cache.GetPersistentCounters().GetValueOrDefault("manuallyMigratedRecords"));
    }

    [Theory]
    [InlineData("receipt")]
    [InlineData("secondRecord")]
    [InlineData("counter")]
    public async Task StorageFailureRollsBackEveryRecordReceiptAndCounterAndAllowsSameCommandRetry(string failure)
    {
        using var data = TemporaryDirectory.Create();
        await using var runtime = RuntimeTesting.CreateRuntime([], data.Path);
        runtime.StartCore();
        var cache = runtime.Deliveries;
        await cache.EnqueueAsync([Draft(1), Draft(1)], Token);
        var command = Selected(cache.ReadPending(1, 0, 10).Select(Item).ToArray());
        var trigger = failure switch
        {
            "receipt" => "BEFORE INSERT ON delivery_migration_receipt",
            "secondRecord" => "BEFORE UPDATE ON delivery WHEN OLD.seq=2",
            _ => "BEFORE INSERT ON queue_counters WHEN NEW.name='manuallyMigratedRecords'",
        };
        Sql(data.Path, $"CREATE TRIGGER fail_migration {trigger} BEGIN SELECT abs(-9223372036854775808); END;");
        Assert.Equal(DeliveryWriteStatus.StorageFailed, (await cache.MigrateAsync(command)).Status);
        Assert.Equal(2, cache.ReadPending(1, 0, 10).Count);
        Assert.All(cache.ReadPending(1, 0, 10), record => { Assert.Null(record.MigrationId); Assert.Equal("fp", record.Fingerprint); });
        Assert.Empty(cache.ReadPending(2, 0, 10));
        Assert.Null(cache.ReadMigrationReceipt(command.OperationId));
        Assert.Equal(0L, Scalar(data.Path, "SELECT COUNT(*) FROM delivery_migration_receipt"));
        Assert.Equal(0, cache.GetPersistentCounters().GetValueOrDefault("manuallyMigratedRecords"));
        Sql(data.Path, "DROP TRIGGER fail_migration");
        var recovered = await cache.MigrateAsync(command);
        Assert.True(recovered.Committed, recovered.Error);
        Assert.Equal(2, recovered.AffectedRecords);
        Assert.Equal(recovered.Migration, cache.ReadMigrationReceipt(command.OperationId));
        Assert.Equal(2, cache.GetPersistentCounters()["manuallyMigratedRecords"]);
    }

    [Fact]
    public async Task StorageProtectionRejectsNewMigrationsButStillReturnsCommittedReceipts()
    {
        using var data = TemporaryDirectory.Create();
        DeliveryMigrationCommand command;
        DeliveryMigrationReceipt? receipt;
        await using (var first = RuntimeTesting.CreateRuntime([], data.Path))
        {
            first.StartCore();
            await first.Deliveries.EnqueueAsync([Draft(1)], Token);
            command = Selected(first.Deliveries.ReadPending(1, 0, 10).Select(Item).ToArray());
            receipt = (await first.Deliveries.MigrateAsync(command)).Migration;
            await first.Deliveries.EnqueueAsync([Draft(1)], Token);
            await first.StopAsync(Token);
        }
        await using var runtime = RuntimeTesting.CreateRuntime([], data.Path, configureCache: options => new()
        { DatabasePath = options.DatabasePath, ConfigStoreId = options.ConfigStoreId, MinimumDiskFreeBytes = long.MaxValue });
        runtime.StartCore();
        var cache = runtime.Deliveries;
        Assert.NotNull(receipt);
        Assert.Equal(receipt, (await cache.MigrateAsync(command)).Migration);
        var blocked = Selected(cache.ReadPending(1, 0, 10).Select(Item).ToArray());
        Assert.Equal(DeliveryMigrationErrors.StorageProtection, (await cache.MigrateAsync(blocked)).Error);
        Assert.Null(cache.ReadMigrationReceipt(blocked.OperationId));
        Assert.Single(cache.ReadPending(1, 0, 10));
    }

    [Fact]
    public async Task RestoredDifferentConfigStoreCannotClaimOldReceiptsOrReuseQueueMigrationGeneration()
    {
        using var data = TemporaryDirectory.Create();
        DeliveryMigrationCommand original;
        DeliveryMigrationReceipt? receipt;
        await using (var first = RuntimeTesting.CreateRuntime([], data.Path))
        {
            first.StartCore();
            await first.Deliveries.EnqueueAsync([Draft(1)], Token);
            original = Selected(first.Deliveries.ReadPending(1, 0, 10).Select(Item).ToArray());
            receipt = (await first.Deliveries.MigrateAsync(original)).Migration;
            await first.StopAsync(Token);
        }
        var otherStore = Guid.NewGuid().ToString("D");
        await using var runtime = RuntimeTesting.CreateRuntime([], data.Path, configureCache: options => new()
        { DatabasePath = options.DatabasePath, ConfigStoreId = otherStore, CommitInterval = options.CommitInterval });
        runtime.StartCore();
        var cache = runtime.Deliveries;
        Assert.Null(cache.ReadMigrationReceipt(original.OperationId));
        Assert.Equal(DeliveryMigrationErrors.StoreChanged, (await cache.MigrateAsync(original)).Error);
        var record = Assert.Single(cache.ReadQuarantined(2, 0, 10));
        Assert.Equal(DeliveryQuarantineReasons.ConfigStoreChanged, record.QuarantineReason);
        var newOperation = DeliveryMigrationCommand.Selected(otherStore, original.OperationId, 2, 3, NextFingerprint, NextSummary, [Item(record)]);
        var migrated = await cache.MigrateAsync(newOperation);
        Assert.True(migrated.Committed, migrated.Error);
        Assert.NotNull(receipt);
        Assert.True(migrated.Migration!.MigrationId > receipt.MigrationId);
        Assert.Equal(otherStore, cache.ReadMigrationReceipt(original.OperationId)!.ConfigStoreId);
        Assert.Equal(2L, Scalar(data.Path, "SELECT COUNT(*) FROM delivery_migration_receipt"));
    }

    [Fact]
    public async Task MigrationRestoresOriginalPendingRetentionInsteadOfRetryOrQuarantinedFailureBaseline()
    {
        using var data = TemporaryDirectory.Create();
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-13T00:00:00Z", CultureInfo.InvariantCulture));
        var createdAt = time.GetUtcNow();
        await using (var runtime = RuntimeTesting.CreateRuntime([], data.Path, time: time, configureCache: options => new()
        { DatabasePath = options.DatabasePath, ConfigStoreId = options.ConfigStoreId, MaximumRecordsPerCommit = 1 }))
        {
            runtime.StartCore();
            var cache = runtime.Deliveries;
            var seqs = (await cache.EnqueueAsync([Draft(1), Draft(1)], Token)).Seqs;
            foreach (var seq in seqs)
            { await cache.RecordFailureAsync(new(seq, 1, "bad", true, false), Token); }
            time.Advance(TimeSpan.FromDays(2));
            var failed = cache.ReadFailed(1, 0, 10);
            await cache.RetryFailedAsync([new(failed[0].Seq, failed[0].DeliveryId)], new Dictionary<long, string> { [1] = "fp" }, Token);
            await cache.QuarantineAsync(seqs[1], DeliveryQuarantineReasons.TargetChanged, Token);
            var originals = All(cache, 1).ToArray();
            Assert.NotNull(originals.Single(record => record.State == DeliveryState.Pending).LastRetriedAt);
            Assert.NotNull(originals.Single(record => record.State == DeliveryState.Quarantined).FailedAt);
            var result = await cache.MigrateAsync(Selected(originals.Select(Item).ToArray()));
            Assert.True(result.Committed, result.Error);
            Assert.All(cache.ReadPending(2, 0, 10), record =>
            {
                Assert.Equal(createdAt, record.CreatedAt);
                Assert.Null(record.FailedAt);
                Assert.Null(record.LastRetriedAt);
            });
            await runtime.StopAsync(Token);
        }
        var reopened = RuntimeTesting.CreateDeliveryCache(data.Path, time);
        try
        {
            using var writer = reopened.Store.OpenWriteConnection();
            reopened.Store.ExpirePending(writer);
            Assert.Empty(reopened.ReadPending(2, 0, 10));
            Assert.Equal(2, reopened.GetQueueTotals(2).Expired);
        }
        finally { reopened.Store.ClearReadPool(); }
    }

    [Fact]
    public async Task VersionSixUpgradeAddsEmptyReceiptTableWithoutInventingMigrations()
    {
        using var data = TemporaryDirectory.Create();
        StoredDelivery before;
        await using (var runtime = RuntimeTesting.CreateRuntime([], data.Path))
        {
            runtime.StartCore();
            await runtime.Deliveries.EnqueueAsync([Draft(1)], Token);
            before = Assert.Single(runtime.Deliveries.ReadPending(1, 0, 10));
            await runtime.StopAsync(Token);
        }
        Sql(data.Path, "DROP INDEX ix_delivery_migration_intent; ALTER TABLE delivery DROP COLUMN migration_intent_id; ALTER TABLE delivery DROP COLUMN migration_previous_state; DROP TABLE delivery_migration_hold; DROP TABLE delivery_migration_receipt; PRAGMA user_version=6;");
        var cache = RuntimeTesting.CreateDeliveryCache(data.Path);
        try
        {
            var after = Assert.Single(cache.ReadPending(1, 0, 10));
            AssertFrozen(before, after);
            Assert.Equal(before.ByteSize, after.ByteSize);
            Assert.Null(after.MigrationId);
            Assert.Null(cache.ReadMigrationReceipt(Guid.NewGuid()));
            Assert.Equal(8, cache.GetStatus().Store!.SchemaVersion);
            Assert.Equal(0L, Scalar(data.Path, "SELECT COUNT(*) FROM delivery_migration_receipt"));
        }
        finally { cache.Store.ClearReadPool(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadersSeeRecordsAndReceiptOnlyTogetherAtTheDurableTransactionBoundary(bool commit)
    {
        using var data = TemporaryDirectory.Create();
        DeliveryMigrationCommand command;
        await using (var runtime = RuntimeTesting.CreateRuntime([], data.Path))
        {
            runtime.StartCore();
            await runtime.Deliveries.EnqueueAsync([Draft(1), Draft(1)], Token);
            command = Selected(runtime.Deliveries.ReadPending(1, 0, 10).Select(Item).ToArray());
            await runtime.StopAsync(Token);
        }
        var cache = RuntimeTesting.CreateDeliveryCache(data.Path);
        try
        {
            using var writer = cache.Store.OpenWriteConnection();
            using var transaction = writer.BeginTransaction();
            var result = cache.Store.MigrateDeliveries(writer, transaction, command, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), false);
            Assert.True(result.Committed, result.Error); // Provisional store result; the writer cannot complete the request yet.
            Assert.Equal(2, cache.ReadPending(1, 0, 10).Count);
            Assert.Empty(cache.ReadPending(2, 0, 10));
            Assert.Null(cache.ReadMigrationReceipt(command.OperationId));
            if (commit)
            { transaction.Commit(); }
            else
            { transaction.Rollback(); }
            Assert.Equal(commit ? 0 : 2, cache.ReadPending(1, 0, 10).Count);
            Assert.Equal(commit ? 2 : 0, cache.ReadPending(2, 0, 10).Count);
            Assert.Equal(commit ? result.Migration : null, cache.ReadMigrationReceipt(command.OperationId));
            Assert.Equal(commit ? 2 : 0, cache.GetPersistentCounters().GetValueOrDefault("manuallyMigratedRecords"));
        }
        finally { cache.Store.ClearReadPool(); }
    }

    private static DeliveryMigrationCommand Selected(IReadOnlyList<DeliveryMigrationItem> items) =>
        DeliveryMigrationCommand.Selected(RuntimeTesting.ConfigStoreId, Guid.NewGuid(), 1, 2, NextFingerprint, NextSummary, items);
    private static DeliveryMigrationItem Item(StoredDelivery record) => new(record.Seq, record.DeliveryId, record.Fingerprint, record.MigrationId);
    private static IEnumerable<StoredDelivery> All(DeliveryCache cache, long target) => cache.ReadPending(target, 0, 1000)
        .Concat(cache.ReadFailed(target, 0, 1000)).Concat(cache.ReadQuarantined(target, 0, 1000)).OrderBy(record => record.Seq);
    private static DeliveryDraft Draft(long target, string fingerprint = "fp") => new(Guid.NewGuid(), target, Guid.NewGuid(), "meter", 17,
        "frozen-payload"u8.ToArray(), fingerprint, "owner", "/original-path", 1,
        new Dictionary<string, string> { ["X-NonSecret"] = "frozen" }, 41, "output", "{\"wire\":true}");
    private static void AssertFrozen(StoredDelivery before, StoredDelivery after)
    {
        Assert.Equal(before.Seq, after.Seq);
        Assert.Equal(before.DeliveryId, after.DeliveryId);
        Assert.Equal(before.SourceMessageId, after.SourceMessageId);
        Assert.Equal(before.DeviceKey, after.DeviceKey);
        Assert.Equal(before.RouteId, after.RouteId);
        Assert.Equal(before.NodeLabel, after.NodeLabel);
        Assert.Equal(before.Revision, after.Revision);
        Assert.Equal(before.Payload.ToArray(), after.Payload.ToArray());
        Assert.Equal(before.TargetAddress, after.TargetAddress);
        Assert.Equal(before.QualityOfService, after.QualityOfService);
        Assert.Equal(before.Headers, after.Headers);
        Assert.Equal(before.ProtocolMetadata, after.ProtocolMetadata);
        Assert.Equal(before.CreatedAt, after.CreatedAt);
        Assert.Equal(before.ClockAnomaly, after.ClockAnomaly);
    }
    private static void Sql(string directory, string sql) => Scalar(directory, sql);
    private static object? Scalar(string directory, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(directory, "queue.db")};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }
}
