using Microsoft.Data.Sqlite;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.Runtime.Deliveries;

namespace TenonEdge.IntegrationTests.Runtime;

public sealed class DeliveryPolicyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TargetLimitRejectsOnlyThatTargetsFanoutAndCommitsTheRest(bool bytes)
    {
        using var data = TemporaryDirectory.Create();
        var original = Draft(1);
        await using var runtime = RuntimeTesting.CreateRuntime([], data.Path, configureCache: options => Options(options,
            count: bytes ? 100 : 1, bytes: bytes ? original.ByteSize : 100000));
        runtime.StartCore();
        Assert.True((await runtime.Deliveries.EnqueueAsync([original], Token)).Committed);
        var result = await runtime.Deliveries.EnqueueAsync([Draft(1), Draft(2)], Token);
        Assert.True(result.Committed);
        Assert.Equal(1, result.QuotaRejectedRecords);
        Assert.Single(result.Seqs);
        Assert.Single(runtime.Deliveries.ReadPending(1, 0, 10));
        Assert.Single(runtime.Deliveries.ReadPending(2, 0, 10));
        Assert.Equal(1, runtime.Deliveries.GetQueueTotals(1).QuotaRejected);
        Assert.Equal(0, runtime.Deliveries.GetQueueTotals(2).QuotaRejected);
        Assert.Equal(1, runtime.Deliveries.GetPersistentCounters()["target/1/quotaRejectedRecords"]);
        Assert.Equal(0, runtime.Deliveries.GetPersistentCounters().GetValueOrDefault("globalQuotaRejectedRecords"));
    }

    [Fact]
    public async Task QuarantineReleasesTargetQuotaButStillConsumesGlobalBytes()
    {
        using var data = TemporaryDirectory.Create();
        var draft = Draft(1);
        await using var runtime = RuntimeTesting.CreateRuntime([], data.Path, configureCache: options => Options(options, count: 1, global: draft.ByteSize * 2));
        runtime.StartCore();
        var cache = runtime.Deliveries;
        var seq = Assert.Single((await cache.EnqueueAsync([draft], Token)).Seqs);
        Assert.True((await cache.QuarantineAsync(seq, DeliveryQuarantineReasons.TargetChanged, Token)).Committed);
        Assert.True((await cache.EnqueueAsync([Draft(1)], Token)).Committed);
        var refused = await cache.EnqueueAsync([Draft(2)], Token);
        Assert.True(refused.Committed);
        Assert.Equal(1, refused.QuotaRejectedRecords);
        Assert.Empty(refused.Seqs);
        var totals = cache.GetQueueTotals(1);
        Assert.Equal(1, totals.PendingCount);
        Assert.Equal(1, totals.QuarantinedCount);
        Assert.Equal(draft.ByteSize, totals.PendingBytes);
        Assert.Equal(draft.ByteSize, totals.QuarantinedBytes);
        await runtime.StopAsync(Token);
        var reopened = RuntimeTesting.CreateDeliveryCache(data.Path);
        Assert.Equal(1, reopened.GetQueueTotals(2).QuotaRejected);
        Assert.Equal(1, reopened.GetPersistentCounters()["globalQuotaRejectedRecords"]);
        Assert.Equal(0, reopened.GetPersistentCounters().GetValueOrDefault("target/2/quotaRejectedRecords"));
        Assert.Equal(totals, reopened.GetQueueTotals(1));
        reopened.Store.ClearReadPool();
    }

    [Fact]
    public async Task FailedRecordsDoNotConsumePendingOrGlobalQuota()
    {
        using var data = TemporaryDirectory.Create();
        var draft = Draft(1);
        await using var runtime = RuntimeTesting.CreateRuntime([], data.Path, configureCache: options => Options(options, count: 1, global: draft.ByteSize));
        runtime.StartCore();
        var cache = runtime.Deliveries;
        var seq = Assert.Single((await cache.EnqueueAsync([draft], Token)).Seqs);
        await cache.RecordFailureAsync(new(seq, 0, "rejected", true, false), Token);
        var result = await cache.EnqueueAsync([Draft(1)], Token);
        Assert.Single(result.Seqs);
        Assert.Equal(0, result.QuotaRejectedRecords);
        Assert.Single(cache.ReadFailed(1, 0, 10));
    }

    [Fact]
    public async Task ReconciliationQuarantinesChangedAndMissingTargetsWithoutChangingFrozenData()
    {
        using var data = TemporaryDirectory.Create();
        await using var runtime = RuntimeTesting.CreateRuntime([], data.Path);
        runtime.StartCore();
        var cache = runtime.Deliveries;
        var draft = Draft(1);
        var seqs = (await cache.EnqueueAsync([draft, Draft(2), Draft(3)], Token)).Seqs;
        var original = Assert.Single(cache.ReadPending(1, 0, 10));
        await cache.RecordFailureAsync(new(seqs[0], 10, "poison", true, true), Token);
        Assert.True((await cache.ReconcileAsync(new Dictionary<long, string> { [1] = "different", [3] = "fp" }, true, Token)).Committed);
        var changed = Assert.Single(cache.ReadQuarantined(1, 0, 10));
        Assert.Equal(DeliveryQuarantineReasons.TargetChanged, changed.QuarantineReason);
        Assert.Equal(original.DeliveryId, changed.DeliveryId);
        Assert.Equal(original.Payload.ToArray(), changed.Payload.ToArray());
        Assert.Equal(original.CreatedAt, changed.CreatedAt);
        Assert.Equal(original.TargetAddress, changed.TargetAddress);
        Assert.Equal(original.Fingerprint, changed.Fingerprint);
        Assert.Equal(10, changed.FailureCount);
        Assert.NotNull(changed.FailedAt);
        Assert.Equal(DeliveryQuarantineReasons.TargetMissing, Assert.Single(cache.ReadQuarantined(2, 0, 10)).QuarantineReason);
        Assert.Single(cache.ReadPending(3, 0, 10));
        await cache.ReconcileAsync(new Dictionary<long, string> { [1] = "fp", [2] = "fp", [3] = "fp" }, true, Token);
        Assert.Single(cache.ReadQuarantined(1, 0, 10));
        Assert.Equal(1, cache.GetQueueTotals(1).Quarantined);
    }

    [Fact]
    public async Task DifferentConfigStoreQuarantinesOldDataOnceAndAcceptsNewData()
    {
        using var data = TemporaryDirectory.Create();
        await using (var old = RuntimeTesting.CreateRuntime([], data.Path))
        {
            old.StartCore();
            await old.Deliveries.EnqueueAsync([Draft(1)], Token);
            await old.StopAsync(Token);
        }

        var newId = Guid.NewGuid().ToString("D");
        await using var runtime = RuntimeTesting.CreateRuntime([], data.Path, configureCache: options => new DeliveryCacheOptions
        {
            DatabasePath = options.DatabasePath,
            ConfigStoreId = newId,
            CommitInterval = options.CommitInterval,
        });
        runtime.StartCore();
        Assert.True((await runtime.Deliveries.EnqueueAsync([Draft(1)], Token)).Committed);
        Assert.Equal(newId, runtime.Deliveries.GetStatus().Store!.ConfigStoreId);
        Assert.Equal(DeliveryQuarantineReasons.ConfigStoreChanged, Assert.Single(runtime.Deliveries.ReadQuarantined(1, 0, 10)).QuarantineReason);
        Assert.Single(runtime.Deliveries.ReadPending(1, 0, 10));
        Assert.Equal(1, runtime.Deliveries.GetQueueTotals(1).Quarantined);
    }

    [Fact]
    public async Task QuarantineAndQuotaCountersRollBackWithFailedTransaction()
    {
        using var data = TemporaryDirectory.Create();
        await using var runtime = RuntimeTesting.CreateRuntime([], data.Path, configureCache: options => Options(options, count: 1));
        runtime.StartCore();
        var cache = runtime.Deliveries;
        var seq = Assert.Single((await cache.EnqueueAsync([Draft(1)], Token)).Seqs);
        Sql(data.Path, "CREATE TRIGGER fail_state BEFORE UPDATE ON delivery BEGIN SELECT RAISE(ABORT, 'fail'); END;");
        Assert.Equal(DeliveryWriteStatus.StorageFailed, (await cache.QuarantineAsync(seq, DeliveryQuarantineReasons.TargetChanged, Token)).Status);
        Assert.Single(cache.ReadPending(1, 0, 10));
        Assert.Equal(0, cache.GetQueueTotals(1).Quarantined);
        Sql(data.Path, "DROP TRIGGER fail_state; CREATE TRIGGER fail_insert BEFORE INSERT ON delivery WHEN NEW.connection_id = 2 BEGIN SELECT abs(-9223372036854775808); END;");
        Assert.Equal(DeliveryWriteStatus.StorageFailed, (await cache.EnqueueAsync([Draft(1), Draft(2)], Token)).Status);
        Assert.Equal(0, cache.GetQueueTotals(1).QuotaRejected);
        Assert.Equal(0, cache.GetPersistentCounters().GetValueOrDefault("target/1/quotaRejectedRecords"));
    }

    [Fact]
    public async Task GlobalQuotaClassificationRollsBackAndTakesPrecedenceWhenBothLimitsAreFull()
    {
        using var data = TemporaryDirectory.Create();
        var draft = Draft(1);
        await using var runtime = RuntimeTesting.CreateRuntime([], data.Path,
            configureCache: options => Options(options, count: 1, global: draft.ByteSize));
        runtime.StartCore();
        var cache = runtime.Deliveries;
        Assert.Single((await cache.EnqueueAsync([draft], Token)).Seqs);
        Sql(data.Path, "CREATE TRIGGER fail_global_counter BEFORE INSERT ON queue_counters WHEN NEW.name = 'globalQuotaRejectedRecords' BEGIN SELECT abs(-9223372036854775808); END;");
        Assert.Equal(DeliveryWriteStatus.StorageFailed, (await cache.EnqueueAsync([Draft(1)], Token)).Status);
        Assert.Equal(0, cache.GetQueueTotals(1).QuotaRejected);
        Assert.Equal(0, cache.GetPersistentCounters().GetValueOrDefault("globalQuotaRejectedRecords"));
        Sql(data.Path, "DROP TRIGGER fail_global_counter;");
        Assert.Equal(1, (await cache.EnqueueAsync([Draft(1)], Token)).QuotaRejectedRecords);
        Assert.Equal(1, cache.GetPersistentCounters()["globalQuotaRejectedRecords"]);
        Assert.Equal(0, cache.GetPersistentCounters().GetValueOrDefault("target/1/quotaRejectedRecords"));
        Assert.Single(cache.ReadPending(1, 0, 10));
    }

    [Fact]
    public async Task PendingAndQuarantinedExpireByOriginalTimeAndAnomalousDatesArePreserved()
    {
        using var data = TemporaryDirectory.Create();
        await using (var runtime = RuntimeTesting.CreateRuntime([], data.Path))
        {
            runtime.StartCore();
            var seqs = (await runtime.Deliveries.EnqueueAsync([Draft(1), Draft(1), Draft(2), Draft(3)], Token)).Seqs;
            await runtime.Deliveries.QuarantineAsync(seqs[1], DeliveryQuarantineReasons.TargetChanged, Token);
            await runtime.StopAsync(Token);
        }

        var now = DateTimeOffset.UtcNow;
        Sql(data.Path, $"UPDATE delivery SET created_at = {now.AddDays(-2).ToUnixTimeMilliseconds()} WHERE connection_id = 1; UPDATE delivery SET created_at = {now.AddDays(2).ToUnixTimeMilliseconds()} WHERE connection_id = 2; UPDATE delivery SET created_at = {now.AddDays(-400).ToUnixTimeMilliseconds()} WHERE connection_id = 3;");
        var cache = RuntimeTesting.CreateDeliveryCache(data.Path);
        using var connection = cache.Store.OpenWriteConnection();
        cache.Store.ExpirePending(connection);
        Assert.Empty(cache.ReadPending(1, 0, 10));
        Assert.Empty(cache.ReadQuarantined(1, 0, 10));
        Assert.Equal(2, cache.GetQueueTotals(1).Expired);
        Assert.True(Assert.Single(cache.ReadPending(2, 0, 10)).ClockAnomaly);
        Assert.True(Assert.Single(cache.ReadPending(3, 0, 10)).ClockAnomaly);
        cache.Store.ExpirePending(connection);
        Assert.Equal(2, cache.GetQueueTotals(1).Expired);
        cache.Store.ClearReadPool();
    }

    [Fact]
    public async Task VersionTwoMigrationPreservesFailureMetadataAndComputesUtf8ByteQuota()
    {
        using var data = TemporaryDirectory.Create();
        var draft = new DeliveryDraft(Guid.NewGuid(), 1, Guid.NewGuid(), "device", 1, "{}"u8.ToArray(), "fp", "账户摘要", "主题", 1,
            new Dictionary<string, string> { ["X-Label"] = "测点" }, nodeLabel: "节点");
        DateTimeOffset? failedAt;
        await using (var runtime = RuntimeTesting.CreateRuntime([], data.Path))
        {
            runtime.StartCore();
            var seq = Assert.Single((await runtime.Deliveries.EnqueueAsync([draft], Token)).Seqs);
            await runtime.Deliveries.RecordFailureAsync(new(seq, 10, "failed", true, true), Token);
            failedAt = Assert.Single(runtime.Deliveries.ReadFailed(1, 0, 10)).FailedAt;
            await runtime.StopAsync(Token);
        }

        Sql(data.Path, "DROP INDEX ix_delivery_migration_intent; ALTER TABLE delivery DROP COLUMN migration_intent_id; ALTER TABLE delivery DROP COLUMN migration_previous_state; DROP TABLE delivery_migration_hold; DROP TABLE delivery_migration_receipt; ALTER TABLE delivery DROP COLUMN last_retried_at; ALTER TABLE delivery DROP COLUMN protocol_metadata; DROP TABLE queue_counters; DROP INDEX ix_delivery_state_created; DROP TABLE delivery_queue_totals; ALTER TABLE delivery DROP COLUMN byte_size; ALTER TABLE delivery DROP COLUMN clock_anomaly; PRAGMA user_version = 2;");
        var cache = RuntimeTesting.CreateDeliveryCache(data.Path);
        var record = Assert.Single(cache.ReadFailed(1, 0, 10));
        Assert.Equal(draft.DeliveryId, record.DeliveryId);
        Assert.Equal(draft.ByteSize, record.ByteSize);
        Assert.Equal(failedAt, record.FailedAt);
        Assert.Equal(10, record.FailureCount);
        Assert.Equal(1, cache.GetFailureTotals(1).Poisoned);
        cache.Store.ClearReadPool();
    }

    private static DeliveryDraft Draft(long id) => new(Guid.NewGuid(), id, Guid.NewGuid(), "device", 1, "{}"u8.ToArray(), "fp", "summary", "topic", 1);
    private static DeliveryCacheOptions Options(DeliveryCacheOptions original, int count = 10000, long bytes = 64000000, long global = 256000000) => new()
    {
        DatabasePath = original.DatabasePath,
        ConfigStoreId = original.ConfigStoreId,
        CommitInterval = original.CommitInterval,
        MaximumPendingPerTarget = count,
        MaximumPendingBytesPerTarget = bytes,
        MaximumQueueBytes = global,
    };
    private static void Sql(string directory, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(directory, "queue.db")};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
    private static CancellationToken Token => TestContext.Current.CancellationToken;
}
