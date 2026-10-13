using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.Runtime.Deliveries;

namespace TenonEdge.IntegrationTests.Runtime;

public sealed class DeliveryMigrationHoldTests
{
    private static readonly string[] MigratedFingerprints = ["new1", "new2"];
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static DeliveryMigrationHold Hold(Guid? id = null) => new(RuntimeTesting.ConfigStoreId, id ?? Guid.NewGuid(), 1, "old");
    private static DeliveryDraft Draft(long connection = 1, string fingerprint = "old") => new(Guid.NewGuid(), connection,
        Guid.NewGuid(), "meter", 1, "private-frozen-body"u8.ToArray(), fingerprint, "safe", "frozen/path", 1,
        headers: new Dictionary<string, string> { ["X-Test"] = "frozen" }, protocolMetadata: "frozen-metadata");

    [Fact]
    public async Task FullRangeIsIsolatedOncePreservesFrozenFieldsAndReturnsOriginalFailuresToFailedArea()
    {
        using var data = TemporaryDirectory.Create();
        await using var runtime = RuntimeTesting.CreateRuntime([], data.Path);
        runtime.StartCore();
        var queue = runtime.Deliveries;
        var seqs = (await queue.EnqueueAsync(Enumerable.Range(0, 1007).Select(_ => Draft()).Concat([Draft(1, "other"), Draft(2)]).ToArray(), Token)).Seqs;
        await queue.RecordFailureAsync(new(seqs[1001], 4, "permanent", true, false), Token);
        await queue.QuarantineAsync(seqs[1002], DeliveryQuarantineReasons.TargetChanged, Token);
        await queue.QuarantineAsync(seqs[1003], DeliveryQuarantineReasons.TargetMissing, Token);
        await queue.QuarantineAsync(seqs[1004], DeliveryQuarantineReasons.ConfigStoreChanged, Token);
        await queue.RecordFailureAsync(new(seqs[1005], 7, "temporary", false, false), Token);
        var before = ReadAll(queue).ToDictionary(row => row.Seq);
        var hold = Hold();
        var held = await runtime.HoldMigrationAsync(hold, null, Token);
        Assert.True(held.Committed, held.Error);
        Assert.Equal(1005, held.AffectedRecords);
        Assert.Equal(held.MigrationHold, queue.ReadMigrationHold(hold.IntentId));
        Assert.Single(queue.ReadPending(1, 0, 10)); // Different fingerprint only.
        Assert.Empty(queue.ReadFailed(1, 0, 10));
        Assert.Equal(1007, Scalar(data.Path, "SELECT COUNT(*) FROM delivery WHERE connection_id=1 AND state='quarantined'"));
        Assert.Equal(1004, queue.GetQueueTotals(1).Quarantined - 3); // Newly isolated, excluding three pre-existing quarantine entries.
        foreach (var row in queue.ReadQuarantined(1, 0, 1000))
        { AssertFrozen(before[row.Seq], row); }
        Assert.Equal(0, queue.GetPersistentCounters().GetValueOrDefault("manuallyMigratedRecords"));
        Assert.Null(queue.ReadMigrationReceipt(hold.IntentId));
        await queue.EnqueueAsync([Draft()], Token);
        Assert.Equal(held.MigrationHold, (await runtime.HoldMigrationAsync(hold, null, Token)).MigrationHold);
        Assert.Equal(2, queue.ReadPending(1, 0, 10).Count); // Replay must not capture the later arrival.
        var migration = DeliveryMigrationCommand.ForIntent(hold, Guid.NewGuid(), "new", "next");
        Assert.Null(queue.PreflightMigration(migration).Error);
        var result = await queue.MigrateAsync(migration);
        Assert.True(result.Committed, result.Error);
        Assert.Equal(1005, result.AffectedRecords);
        var failure = Assert.Single(queue.ReadFailed(1, 0, 10));
        Assert.Equal(seqs[1001], failure.Seq);
        Assert.Equal(before[failure.Seq].FailedAt, failure.FailedAt);
        AssertFrozen(before[failure.Seq], failure);
        Assert.Equal(0, failure.FailureCount);
        Assert.Equal(2, queue.ReadQuarantined(1, 0, 10).Count);
        Assert.Equal(0, Scalar(data.Path, "SELECT COUNT(*) FROM delivery WHERE migration_intent_id IS NOT NULL"));
        Assert.Equal(1005, queue.GetPersistentCounters()["manuallyMigratedRecords"]);
        Assert.Equal(result.Migration, (await queue.MigrateAsync(migration)).Migration);
        Assert.Equal(1005, queue.GetPersistentCounters()["manuallyMigratedRecords"]);
    }

    [Fact]
    public async Task IsolatingFailuresMayExceedGlobalQuotaButMigrationUsesTheirOriginalFailedState()
    {
        using var data = TemporaryDirectory.Create();
        var bytes = Draft().ByteSize;
        await using var runtime = RuntimeTesting.CreateRuntime([], data.Path, configureCache: options => new()
        { DatabasePath = options.DatabasePath, ConfigStoreId = options.ConfigStoreId, CommitInterval = options.CommitInterval, MaximumQueueBytes = bytes });
        runtime.StartCore();
        var queue = runtime.Deliveries;
        var seq = Assert.Single((await queue.EnqueueAsync([Draft()], Token)).Seqs);
        await queue.RecordFailureAsync(new(seq, 1, "bad", true, false), Token);
        var original = Assert.Single(queue.ReadFailed(1, 0, 10));
        await queue.EnqueueAsync([Draft(2)], Token); // Global pending budget exactly full.
        var hold = Hold();
        Assert.True((await runtime.HoldMigrationAsync(hold, null, Token)).Committed);
        Assert.Equal(bytes, queue.GetQueueTotals(1).QuarantinedBytes);
        Assert.Equal(1, (await queue.EnqueueAsync([Draft(3)], Token)).QuotaRejectedRecords);
        var move = DeliveryMigrationCommand.ForIntent(hold, Guid.NewGuid(), "new", "safe");
        Assert.Null(queue.PreflightMigration(move).Error);
        Assert.True((await queue.MigrateAsync(move)).Committed);
        Assert.Equal(original.FailedAt, Assert.Single(queue.ReadFailed(1, 0, 10)).FailedAt);
        Assert.Empty(queue.ReadPending(1, 0, 10));
        Assert.Single(queue.ReadPending(2, 0, 10));
    }

    [Fact]
    public async Task FormBCanExplicitlyOverrideHeldRecordsButOldIntentCannotRecaptureThem()
    {
        using var data = TemporaryDirectory.Create();
        await using var runtime = RuntimeTesting.CreateRuntime([], data.Path);
        runtime.StartCore();
        var queue = runtime.Deliveries;
        var seqs = (await queue.EnqueueAsync([Draft(), Draft()], Token)).Seqs;
        await queue.RecordFailureAsync(new(seqs[0], 2, "bad", true, false), Token);
        var hold = Hold();
        Assert.True((await runtime.HoldMigrationAsync(hold, null, Token)).Committed);
        var selected = queue.ReadQuarantined(1, 0, 10)[0];
        // Even keeping the same connection and fingerprint changes the record generation and clears intent ownership.
        var manual = DeliveryMigrationCommand.Selected(RuntimeTesting.ConfigStoreId, Guid.NewGuid(), 1, 1, "old", "safe",
            [new(selected.Seq, selected.DeliveryId, selected.Fingerprint, selected.MigrationId)]);
        Assert.True((await queue.MigrateAsync(manual)).Committed);
        Assert.Equal(DeliveryState.Pending, Assert.Single(queue.ReadPending(1, 0, 10)).State); // Form B's documented quarantine semantics.
        Assert.Equal(2, (await runtime.HoldMigrationAsync(hold, null, Token)).AffectedRecords); // Original hold receipt, not a new selection.
        Assert.Equal(1, (await queue.MigrateAsync(DeliveryMigrationCommand.ForIntent(hold, Guid.NewGuid(), "new", "next"))).AffectedRecords);
        Assert.Equal("old", queue.ReadPending(1, 0, 10).Single(row => row.Seq == seqs[0]).Fingerprint);
    }

    [Theory]
    [InlineData("isolate")]
    [InlineData("migrate")]
    public async Task TransactionFailureRollsBackReceiptStateAndCountersWithoutReleasingOriginalFailure(string boundary)
    {
        using var data = TemporaryDirectory.Create();
        await using var runtime = RuntimeTesting.CreateRuntime([], data.Path);
        runtime.StartCore();
        var queue = runtime.Deliveries;
        var seqs = (await queue.EnqueueAsync([Draft(), Draft()], Token)).Seqs;
        await queue.RecordFailureAsync(new(seqs[0], 2, "bad", true, false), Token);
        var hold = Hold();
        if (boundary == "migrate")
        { Assert.True((await runtime.HoldMigrationAsync(hold, null, Token)).Committed); }
        Sql(data.Path, $"CREATE TRIGGER fail_intent BEFORE INSERT ON {(boundary == "isolate" ? "delivery_migration_hold" : "delivery_migration_receipt")} BEGIN SELECT abs(-9223372036854775808); END;");
        var command = DeliveryMigrationCommand.ForIntent(hold, Guid.NewGuid(), "new", "next");
        var failed = boundary == "isolate" ? await runtime.HoldMigrationAsync(hold, null, Token) : await queue.MigrateAsync(command);
        Assert.Equal(DeliveryWriteStatus.StorageFailed, failed.Status);
        Assert.Null(queue.ReadMigrationReceipt(command.OperationId));
        Assert.Equal(0, queue.GetPersistentCounters().GetValueOrDefault("manuallyMigratedRecords"));
        if (boundary == "isolate")
        {
            Assert.Null(queue.ReadMigrationHold(hold.IntentId));
            Assert.Single(queue.ReadPending(1, 0, 10));
            Assert.Single(queue.ReadFailed(1, 0, 10));
            Assert.Equal(0, queue.GetQueueTotals(1).Quarantined);
        }
        else
        { Assert.Equal(2, queue.ReadQuarantined(1, 0, 10).Count); }
        Sql(data.Path, "DROP TRIGGER fail_intent");
        Assert.True((await runtime.HoldMigrationAsync(hold, null, Token)).Committed);
        Assert.True((await queue.MigrateAsync(command)).Committed);
        Assert.Equal(seqs[0], Assert.Single(queue.ReadFailed(1, 0, 10)).Seq);
        Assert.Single(queue.ReadPending(1, 0, 10));
        Assert.Equal(2, queue.GetQueueTotals(1).Quarantined);
    }

    [Fact]
    public async Task MissingOrChangedHoldAndForeignStoreCannotMigrateUnheldRecords()
    {
        using var data = TemporaryDirectory.Create();
        await using var runtime = RuntimeTesting.CreateRuntime([], data.Path);
        runtime.StartCore();
        var queue = runtime.Deliveries;
        await queue.EnqueueAsync([Draft()], Token);
        var hold = Hold();
        Assert.Equal(DeliveryMigrationErrors.SelectionChanged,
            (await queue.MigrateAsync(DeliveryMigrationCommand.ForIntent(hold, Guid.NewGuid(), "new", "next"))).Error);
        Assert.True((await runtime.HoldMigrationAsync(hold, null, Token)).Committed);
        var changed = new DeliveryMigrationHold(hold.ConfigStoreId, hold.IntentId, 1, "changed");
        Assert.Equal(DeliveryMigrationErrors.OperationChanged, (await runtime.HoldMigrationAsync(changed, null, Token)).Error);
        Assert.Equal(DeliveryMigrationErrors.SelectionChanged, queue.PreflightMigration(DeliveryMigrationCommand.ForIntent(changed, Guid.NewGuid(), "new", "next")).Error);
        var foreign = new DeliveryMigrationHold(Guid.NewGuid().ToString("D"), hold.IntentId, 1, "old");
        Assert.Equal(DeliveryMigrationErrors.StoreChanged, (await runtime.HoldMigrationAsync(foreign, null, Token)).Error);
        Assert.Equal(0, (await queue.MigrateAsync(DeliveryMigrationCommand.OldTarget(hold.ConfigStoreId, Guid.NewGuid(), 1, "old", "new", "next"))).AffectedRecords);
        Assert.Single(queue.ReadQuarantined(1, 0, 10));
    }

    [Fact]
    public async Task ConsecutiveIntentsOwnDisjointRangesAndReplaySurvivesRestart()
    {
        using var data = TemporaryDirectory.Create();
        var first = Hold();
        var second = Hold();
        await using (var runtime = RuntimeTesting.CreateRuntime([], data.Path))
        {
            runtime.StartCore();
            await runtime.Deliveries.EnqueueAsync([Draft()], Token);
            Assert.Equal(1, (await runtime.HoldMigrationAsync(first, null, Token)).AffectedRecords);
            await runtime.Deliveries.EnqueueAsync([Draft()], Token);
            Assert.Equal(1, (await runtime.HoldMigrationAsync(second, null, Token)).AffectedRecords);
        }
        await using var restarted = RuntimeTesting.CreateRuntime([], data.Path);
        restarted.StartCore();
        Assert.Equal(1, (await restarted.HoldMigrationAsync(first, null, Token)).AffectedRecords);
        Assert.Equal(1, (await restarted.Deliveries.MigrateAsync(DeliveryMigrationCommand.ForIntent(first, Guid.NewGuid(), "new1", "next"))).AffectedRecords);
        Assert.Equal(1, (await restarted.Deliveries.MigrateAsync(DeliveryMigrationCommand.ForIntent(second, Guid.NewGuid(), "new2", "next"))).AffectedRecords);
        Assert.Equal(MigratedFingerprints, restarted.Deliveries.ReadPending(1, 0, 10).Select(row => row.Fingerprint));
    }

    [Fact]
    public async Task VersionSevenUpgradePreservesExistingMigrationReceiptsAndFrozenRecords()
    {
        using var data = TemporaryDirectory.Create();
        var command = DeliveryMigrationCommand.OldTarget(RuntimeTesting.ConfigStoreId, Guid.NewGuid(), 1, "old", "new", "next");
        DeliveryMigrationReceipt receipt;
        StoredDelivery before;
        await using (var runtime = RuntimeTesting.CreateRuntime([], data.Path))
        {
            runtime.StartCore();
            await runtime.Deliveries.EnqueueAsync([Draft()], Token);
            receipt = (await runtime.Deliveries.MigrateAsync(command)).Migration!;
            before = Assert.Single(runtime.Deliveries.ReadPending(1, 0, 10));
        }
        // Reconstruct the exact v7 schema (v8 only added these columns/index/table), retaining its real v7 receipt data.
        Sql(data.Path, "ALTER TABLE delivery DROP COLUMN response_summary; DROP INDEX ix_delivery_migration_intent; ALTER TABLE delivery DROP COLUMN migration_intent_id; ALTER TABLE delivery DROP COLUMN migration_previous_state; DROP TABLE delivery_migration_hold; PRAGMA user_version=7;");
        await using var reopened = RuntimeTesting.CreateRuntime([], data.Path);
        reopened.StartCore();
        Assert.Equal(receipt, reopened.Deliveries.ReadMigrationReceipt(command.OperationId));
        AssertFrozen(before, Assert.Single(reopened.Deliveries.ReadPending(1, 0, 10)));
        Assert.Equal(receipt, (await reopened.Deliveries.MigrateAsync(command)).Migration);
        Assert.Equal(1, reopened.Deliveries.GetPersistentCounters()["manuallyMigratedRecords"]);
        Assert.Equal(9, reopened.Deliveries.GetStatus().Store!.SchemaVersion);
        Assert.Equal(0, Scalar(data.Path, "SELECT COUNT(*) FROM delivery_migration_hold"));
        Assert.Equal(0, Scalar(data.Path, "SELECT COUNT(*) FROM delivery WHERE migration_intent_id IS NOT NULL OR migration_previous_state IS NOT NULL"));
    }

    [Fact]
    public async Task QuotaRefusalKeepsWholeIntentIsolatedAndRetryDoesNotRefreshFailureOrCreationTimes()
    {
        using var data = TemporaryDirectory.Create();
        await using var runtime = RuntimeTesting.CreateRuntime([], data.Path, configureCache: options => new()
        { DatabasePath = options.DatabasePath, ConfigStoreId = options.ConfigStoreId, CommitInterval = options.CommitInterval, MaximumPendingPerTarget = 2 });
        runtime.StartCore();
        var queue = runtime.Deliveries;
        var seqs = (await queue.EnqueueAsync([Draft(), Draft()], Token)).Seqs;
        await queue.RecordFailureAsync(new(seqs[0], 1, "failed", true, false), Token);
        var failed = Assert.Single(queue.ReadFailed(1, 0, 10));
        var hold = Hold();
        await runtime.HoldMigrationAsync(hold, null, Token);
        var fresh = Assert.Single((await queue.EnqueueAsync([Draft(1, "new")], Token)).Seqs);
        var move = DeliveryMigrationCommand.ForIntent(hold, Guid.NewGuid(), "new", "next");
        Assert.Equal(1, queue.PreflightMigration(move).Quota!.PendingRecords);
        var denied = await queue.MigrateAsync(move);
        Assert.Equal(DeliveryMigrationErrors.QuotaExceeded, denied.Error);
        Assert.Equal(1, denied.MigrationQuota!.PendingRecords);
        Assert.Equal(2, queue.ReadQuarantined(1, 0, 10).Count);
        Assert.Null(queue.ReadMigrationReceipt(move.OperationId));
        await queue.DeleteAsync([fresh], Token);
        Assert.True((await queue.MigrateAsync(DeliveryMigrationCommand.ForIntent(hold, Guid.NewGuid(), "new", "next"))).Committed);
        AssertFrozen(failed, Assert.Single(queue.ReadFailed(1, 0, 10)));
        Assert.Equal(failed.FailedAt, Assert.Single(queue.ReadFailed(1, 0, 10)).FailedAt);
    }

    private static IEnumerable<StoredDelivery> ReadAll(DeliveryCache queue) =>
        queue.ReadPending(1, 0, 1000).Concat(queue.ReadPending(1, queue.ReadPending(1, 0, 1000)[^1].Seq, 1000))
            .Concat(queue.ReadFailed(1, 0, 1000)).Concat(queue.ReadQuarantined(1, 0, 1000));
    private static void AssertFrozen(StoredDelivery before, StoredDelivery after)
    {
        Assert.Equal((before.Seq, before.DeliveryId, before.SourceMessageId, before.DeviceKey, before.Revision, before.CreatedAt),
            (after.Seq, after.DeliveryId, after.SourceMessageId, after.DeviceKey, after.Revision, after.CreatedAt));
        Assert.Equal(before.Payload.ToArray(), after.Payload.ToArray());
        Assert.Equal((before.TargetAddress, before.QualityOfService, before.ProtocolMetadata),
            (after.TargetAddress, after.QualityOfService, after.ProtocolMetadata));
        Assert.Equal(before.Headers, after.Headers);
    }
    private static long Scalar(string path, string sql)
    {
        using var database = new SqliteConnection($"Data Source={Path.Combine(path, "queue.db")};Pooling=False");
        database.Open();
        using var command = database.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }
    private static void Sql(string path, string sql) => _ = Scalar(path, sql);
}
