using Microsoft.Data.Sqlite;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.Runtime.Deliveries;

namespace TenonEdge.IntegrationTests.Runtime;

public sealed class DeliveryFailureStoreTests
{
    [Theory]
    [InlineData(1, 10, 1, 2)]
    [InlineData(10, 1, 2, 1)]
    public async Task TargetAndGlobalLimitsDropOnlyNewFailureAndPersistCounters(int targetLimit, int globalLimit, long overflowTarget, int expectedFailed)
    {
        using var directory = TemporaryDirectory.Create();
        await using var runtime = RuntimeTesting.CreateRuntime([], directory.Path, configureCache: original => new DeliveryCacheOptions
        {
            DatabasePath = original.DatabasePath,
            ConfigStoreId = original.ConfigStoreId,
            CommitInterval = original.CommitInterval,
            MaximumFailedPerTarget = targetLimit,
            MaximumFailedGlobal = globalLimit,
        });
        runtime.StartCore();
        var cache = runtime.Deliveries;
        var inserted = await cache.EnqueueAsync([Draft(1), Draft(overflowTarget), Draft(2)], TestContext.Current.CancellationToken);
        foreach (var seq in inserted.Seqs.Take(2))
        {
            Assert.True((await cache.RecordFailureAsync(new(seq, 0, "rejected", true, false), TestContext.Current.CancellationToken)).Committed);
        }

        Assert.True((await cache.RecordFailureAsync(new(inserted.Seqs[2], 0, "rejected", true, false), TestContext.Current.CancellationToken)).Committed);
        Assert.Equal(expectedFailed, cache.ReadFailed(1, 0, 10).Count + cache.ReadFailed(2, 0, 10).Count);
        Assert.Empty(cache.ReadPending(1, 0, 10));
        Assert.Empty(cache.ReadPending(2, 0, 10));
        Assert.Equal(3, cache.GetFailureTotals(1).Rejected + cache.GetFailureTotals(2).Rejected);
        Assert.Equal(3 - expectedFailed, cache.GetFailureTotals(1).Overflowed + cache.GetFailureTotals(2).Overflowed);
        var before = cache.GetFailureTotals(overflowTarget);
        // 已处理结果再提交，不增加计数，也不会换掉已保留的失败记录。
        Assert.True((await cache.RecordFailureAsync(new(inserted.Seqs[1], 0, "rejected", true, false), TestContext.Current.CancellationToken)).Committed);
        Assert.Equal(before, cache.GetFailureTotals(overflowTarget));
        await runtime.StopAsync(TestContext.Current.CancellationToken);
        var reopened = RuntimeTesting.CreateDeliveryCache(directory.Path);
        Assert.Equal(before, reopened.GetFailureTotals(overflowTarget));
        reopened.Store.ClearReadPool();
    }

    [Fact]
    public async Task CounterWriteFailureRollsBackStateChangeAndAbsoluteFailureCountIsIdempotent()
    {
        using var directory = TemporaryDirectory.Create();
        await using var runtime = RuntimeTesting.CreateRuntime([], directory.Path);
        runtime.StartCore();
        var cache = runtime.Deliveries;
        var seq = Assert.Single((await cache.EnqueueAsync([Draft(1)], TestContext.Current.CancellationToken)).Seqs);
        for (var i = 0; i < 2; i++)
        {
            Assert.True((await cache.RecordFailureAsync(new(seq, 10, "retry", false, false), TestContext.Current.CancellationToken)).Committed);
        }

        Assert.Equal(10, Assert.Single(cache.ReadPending(1, 0, 10)).FailureCount);
        Sql(directory.Path, "CREATE TRIGGER fail_totals BEFORE INSERT ON delivery_failure_totals BEGIN SELECT RAISE(ABORT, 'counter failure'); END;");
        var failure = await cache.RecordFailureAsync(new(seq, 11, "poison", true, true), TestContext.Current.CancellationToken);
        Assert.Equal(DeliveryWriteStatus.StorageFailed, failure.Status);
        Assert.Equal(10, Assert.Single(cache.ReadPending(1, 0, 10)).FailureCount);
        Assert.Empty(cache.ReadFailed(1, 0, 10));
        Assert.Equal(new DeliveryFailureTotals(0, 0, 0, 0), cache.GetFailureTotals(1));
        Sql(directory.Path, "DROP TRIGGER fail_totals");
        Assert.True((await cache.RecordFailureAsync(new(seq, 11, "poison", true, true), TestContext.Current.CancellationToken)).Committed);
        Assert.Equal(11, Assert.Single(cache.ReadFailed(1, 0, 10)).FailureCount);
        Assert.Equal(1, cache.GetFailureTotals(1).Poisoned);
    }

    [Fact]
    public async Task FailedRetentionIsSeparateAndClockJumpDoesNotExpireRecords()
    {
        using var directory = TemporaryDirectory.Create();
        await using (var runtime = RuntimeTesting.CreateRuntime([], directory.Path))
        {
            runtime.StartCore();
            var seqs = (await runtime.Deliveries.EnqueueAsync([Draft(1), Draft(1)], TestContext.Current.CancellationToken)).Seqs;
            await runtime.Deliveries.RecordFailureAsync(new(seqs[0], 0, "rejected", true, false), TestContext.Current.CancellationToken);
            await runtime.StopAsync(TestContext.Current.CancellationToken);
        }

        var clock = new MutableClock();
        var cache = RuntimeTesting.CreateDeliveryCache(directory.Path, clock);
        using var connection = cache.Store.OpenWriteConnection();
        cache.Store.ExpireFailures(connection); // 建立单调时间基线。
        clock.Utc += TimeSpan.FromDays(8); // 墙钟跳变，无单调时间经过。
        cache.Store.ExpireFailures(connection);
        Assert.Single(cache.ReadFailed(1, 0, 10));
        clock.Elapsed += TimeSpan.FromDays(8);
        cache.Store.ExpireFailures(connection);
        Assert.Empty(cache.ReadFailed(1, 0, 10));
        Assert.Single(cache.ReadPending(1, 0, 10));
        Assert.Equal(1, cache.GetFailureTotals(1).Expired);
        cache.Store.ExpireFailures(connection);
        Assert.Equal(1, cache.GetFailureTotals(1).Expired);
        cache.Store.ClearReadPool();
    }

    [Fact]
    public async Task MigrationFromVersionOnePreservesPendingRecordAndIdentity()
    {
        using var directory = TemporaryDirectory.Create();
        var draft = Draft(1);
        await using (var runtime = RuntimeTesting.CreateRuntime([], directory.Path))
        {
            runtime.StartCore();
            await runtime.Deliveries.EnqueueAsync([draft], TestContext.Current.CancellationToken);
            await runtime.StopAsync(TestContext.Current.CancellationToken);
        }

        // 还原 T1-03/04 的真实表结构，再由新版启动迁移。
        Sql(directory.Path, "DROP INDEX ix_delivery_state_connection_bytes; ALTER TABLE delivery DROP COLUMN response_summary; DROP INDEX ix_delivery_migration_intent; ALTER TABLE delivery DROP COLUMN migration_intent_id; ALTER TABLE delivery DROP COLUMN migration_previous_state; DROP TABLE delivery_migration_hold; DROP TABLE delivery_migration_receipt; ALTER TABLE delivery DROP COLUMN last_retried_at; ALTER TABLE delivery DROP COLUMN protocol_metadata; DROP TABLE queue_counters; DROP INDEX ix_delivery_state_created; DROP TABLE delivery_queue_totals; ALTER TABLE delivery DROP COLUMN byte_size; ALTER TABLE delivery DROP COLUMN clock_anomaly; DROP INDEX ix_delivery_failed_at; ALTER TABLE delivery DROP COLUMN failed_at; DROP TABLE delivery_failure_totals; PRAGMA user_version = 1;");
        var cache = RuntimeTesting.CreateDeliveryCache(directory.Path);
        var record = Assert.Single(cache.ReadPending(1, 0, 10));
        Assert.Equal(draft.DeliveryId, record.DeliveryId);
        Assert.Equal(draft.Payload.ToArray(), record.Payload.ToArray());
        Assert.Null(record.FailedAt);
        Assert.Equal(10, cache.GetStatus().Store!.SchemaVersion);
        Assert.Equal(RuntimeTesting.ConfigStoreId, cache.GetStatus().Store!.ConfigStoreId);
        cache.Store.ClearReadPool();
    }

    private static DeliveryDraft Draft(long connection) => new(Guid.NewGuid(), connection, Guid.NewGuid(), "device", 1,
        "{}"u8.ToArray(), "fp", "summary", targetAddress: "topic", qualityOfService: 1);

    private static void Sql(string directory, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(directory, "queue.db")};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private sealed class MutableClock : TimeProvider
    {
        public DateTimeOffset Utc { get; set; } = DateTimeOffset.UtcNow;
        public TimeSpan Elapsed { get; set; }
        public override DateTimeOffset GetUtcNow() => Utc;
        public override long GetTimestamp() => Elapsed.Ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    }
}
