using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.Runtime.Deliveries;
using TenonEdge.Runtime.Diagnostics;

namespace TenonEdge.IntegrationTests.Runtime;

/// <summary>
/// 发送缓存 queue.db（T1-03；总设计 §8.2、§8.5，GW-14，V-07 复验），全部使用磁盘上的真实 SQLite 文件：
/// WAL 与 synchronous=FULL 显式设置并回读；只有分组提交返回后才报告接管，读取只能读到已提交的记录；
/// 提交失败时整批回滚并如实报告；报文与目标冻结，重启后原样保留；同一目标内按 seq 先进先出，大量积压分页读取不重不漏。
/// </summary>
public sealed class DeliveryCacheTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task OpeningCreatesAPrivateWalDatabaseWithFullSynchronousWrites()
    {
        using var directory = TemporaryDirectory.Create();
        await using var writer = WriterHarness.Start(directory.Path);

        var result = await writer.EnqueueAsync([Draft()]);

        Assert.True(result.Committed);
        var info = Assert.IsType<DeliveryStoreInfo>(writer.Cache.GetStatus().Store);
        Assert.True(Version.Parse(info.SqliteVersion) >= new Version(3, 51, 3), info.SqliteVersion);
        Assert.Equal("wal", info.JournalMode);
        Assert.Equal(2, info.Synchronous);
        Assert.Equal(1, info.SchemaVersion);
        Assert.Equal(RuntimeTesting.ConfigStoreId, info.ConfigStoreId);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(info.FilePath));
        }

        using var connection = Open(info.FilePath);
        Assert.Equal(1L, Scalar(connection, "PRAGMA user_version;"));
        Assert.Equal(RuntimeTesting.ConfigStoreId, Scalar(connection, "SELECT config_store_id FROM queue_meta;"));
    }

    [Fact]
    public async Task CommittedRecordsAreReadBackExactlyAsFrozen()
    {
        using var directory = TemporaryDirectory.Create();
        await using var writer = WriterHarness.Start(directory.Path);
        var headers = new Dictionary<string, string> { ["X-Tenant"] = "plant-1", ["Accept"] = "application/json" };
        var draft = Draft(payload: """{"schemaVersion":1,"values":{"energy":18446744073709551615}}""", headers: headers, routeId: 42, nodeLabel: "out-1");
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);

        var result = await writer.EnqueueAsync([draft]);

        var stored = Assert.Single(writer.Cache.ReadPending(draft.ConnectionId, afterSeq: 0, limit: 10));
        Assert.Equal(Assert.Single(result.Seqs), stored.Seq);
        Assert.Equal(draft.DeliveryId, stored.DeliveryId);
        Assert.Equal(draft.SourceMessageId, stored.SourceMessageId);
        Assert.Equal(draft.DeviceKey, stored.DeviceKey);
        Assert.Equal(draft.Revision, stored.Revision);
        Assert.Equal(draft.Payload.ToArray(), stored.Payload.ToArray());
        Assert.Equal("tenonedge/meter-001", stored.TargetAddress);
        Assert.Equal(1, stored.QualityOfService);
        Assert.Equal(["Accept", "X-Tenant"], stored.Headers!.Keys);
        Assert.Equal("plant-1", stored.Headers["X-Tenant"]);
        Assert.Equal((42L, "out-1"), (stored.RouteId!.Value, stored.NodeLabel!));
        Assert.Equal(("fp-1", "mqtt://broker.local:1883 账户 gateway"), (stored.Fingerprint, stored.FingerprintSummary));
        Assert.Equal(DeliveryState.Pending, stored.State);
        Assert.Equal(0, stored.FailureCount);
        Assert.InRange(stored.CreatedAt, before, DateTimeOffset.UtcNow.AddSeconds(1));
    }

    [Fact]
    public async Task AFailedCommitRollsBackTheWholeBatchAndIsReportedAsNotTakenOver()
    {
        using var directory = TemporaryDirectory.Create();

        // 提交间隔放长，使两个请求落进同一个事务；其中一条记录触发写入失败，整批回滚。
        await using var writer = WriterHarness.Start(directory.Path, options => WithInterval(options, TimeSpan.FromMilliseconds(500)));
        await writer.EnqueueAsync([Draft()]);
        using (var connection = Open(writer.Cache.Store.FilePath))
        {
            // 触发器中的整数溢出是一般的 SQLite 错误（SQLITE_ERROR），与磁盘写满等存储错误同类；RAISE 则属于约束错误，另见下一个用例。
            Execute(connection, "CREATE TRIGGER fail_on_broken BEFORE INSERT ON delivery WHEN NEW.device_key = 'broken' BEGIN SELECT abs(-9223372036854775808); END;");
        }

        var healthy = writer.EnqueueAsync([Draft(connectionId: 2), Draft(connectionId: 3)]);
        var broken = writer.EnqueueAsync([Draft(connectionId: 2), Draft(connectionId: 3, deviceKey: "broken")]);
        var results = await Task.WhenAll(healthy, broken).WaitAsync(Timeout, TestContext.Current.CancellationToken);

        Assert.All(results, result =>
        {
            Assert.False(result.Committed);
            Assert.Equal(DeliveryWriteStatus.StorageFailed, result.Status);
            Assert.Empty(result.Seqs);
            Assert.Contains("integer overflow", result.Error, StringComparison.Ordinal);
        });
        Assert.Empty(writer.Cache.ReadPending(2, 0, 10));
        Assert.Empty(writer.Cache.ReadPending(3, 0, 10));
        var faulted = writer.Cache.GetStatus();
        Assert.True(faulted.StorageFault);
        Assert.Equal((1L, 4L), (faulted.FailedBatches, faulted.FailedRecords));
        Assert.Single(writer.Events.OfType(RuntimeEventTypes.StorageFault));

        // 之后的批次照常提交，状态恢复。
        var recovered = await writer.EnqueueAsync([Draft(connectionId: 2)]);
        Assert.True(recovered.Committed);
        Assert.False(writer.Cache.GetStatus().StorageFault);
        Assert.Single(writer.Events.OfType(RuntimeEventTypes.StorageRecovered));
        Assert.Single(writer.Cache.ReadPending(2, 0, 10));
    }

    [Fact]
    public async Task ARequestThatViolatesAConstraintIsRejectedAloneAndIsNotAStorageFault()
    {
        using var directory = TemporaryDirectory.Create();
        await using var writer = WriterHarness.Start(directory.Path, options => WithInterval(options, TimeSpan.FromMilliseconds(500)));
        var existing = Draft(connectionId: 1);
        await writer.EnqueueAsync([existing]);

        // 同一事务中：一个请求的投递编号与已提交的记录重复（例如调用方放弃等待后用同一草稿重试），另一个请求正常。
        var duplicate = new DeliveryDraft(existing.DeliveryId, 2, Guid.CreateVersion7(), "meter-001", 1, "{}"u8.ToArray(), "fp-1", "summary");
        var rejected = writer.EnqueueAsync([duplicate, Draft(connectionId: 2)]);
        var healthy = writer.EnqueueAsync([Draft(connectionId: 3)]);
        var results = await Task.WhenAll(rejected, healthy);

        Assert.False(results[0].Committed);
        Assert.Equal(DeliveryWriteStatus.Rejected, results[0].Status);
        Assert.Contains("数据错误", results[0].Error, StringComparison.Ordinal);
        Assert.True(results[1].Committed, results[1].Error);
        Assert.Empty(writer.Cache.ReadPending(2, 0, 10));
        Assert.Single(writer.Cache.ReadPending(3, 0, 10));
        var status = writer.Cache.GetStatus();
        Assert.False(status.StorageFault);
        Assert.Equal((2L, 0L, 0L), (status.RejectedRecords, status.FailedBatches, status.FailedRecords));
        Assert.Empty(writer.Events.OfType(RuntimeEventTypes.StorageFault));
    }

    [Fact]
    public async Task ReadersSeeOnlyCommittedRecords()
    {
        using var directory = TemporaryDirectory.Create();
        await using var writer = WriterHarness.Start(directory.Path);
        await writer.EnqueueAsync([Draft(connectionId: 1)]);
        var stored = Assert.Single(writer.Cache.ReadPending(1, 0, 10));

        // 另一个连接中尚未提交的写入（相当于写者事务进行到一半）对读取不可见。
        using var connection = Open(writer.Cache.Store.FilePath);
        using (var transaction = connection.BeginTransaction())
        {
            Execute(
                connection,
                $"""
                INSERT INTO delivery (delivery_id, connection_id, source_message_id, device_key, revision, payload, fingerprint, fingerprint_summary, created_at)
                SELECT '{Guid.NewGuid()}', 1, source_message_id, device_key, revision, payload, fingerprint, fingerprint_summary, created_at FROM delivery WHERE seq = {stored.Seq};
                """,
                transaction);
            Assert.Single(writer.Cache.ReadPending(1, 0, 10));
            transaction.Commit();
        }

        Assert.Equal(2, writer.Cache.ReadPending(1, 0, 10).Count);
    }

    [Fact]
    public async Task CommittedRecordsSurviveRestartingTheWriter()
    {
        using var directory = TemporaryDirectory.Create();
        List<DeliveryDraft> drafts = [Draft(connectionId: 1), Draft(connectionId: 1), Draft(connectionId: 2)];
        IReadOnlyList<long> seqs;
        await using (var writer = WriterHarness.Start(directory.Path))
        {
            seqs = (await writer.EnqueueAsync(drafts)).Seqs;
        }

        await using var restarted = WriterHarness.Start(directory.Path);
        var first = restarted.Cache.ReadPending(1, 0, 10);
        var second = restarted.Cache.ReadPending(2, 0, 10);

        Assert.Equal(seqs, first.Concat(second).Select(record => record.Seq));
        Assert.Equal(drafts.Select(draft => draft.DeliveryId), first.Concat(second).Select(record => record.DeliveryId));
        Assert.All(first.Concat(second).Zip(drafts), pair => Assert.Equal(pair.Second.Payload.ToArray(), pair.First.Payload.ToArray()));
    }

    [Fact]
    public async Task ALargeBacklogIsReadInFifoOrderExactlyOnceWhileAcknowledgedRecordsAreDeleted()
    {
        using var directory = TemporaryDirectory.Create();
        await using var writer = WriterHarness.Start(directory.Path);
        var expected = new List<Guid>();
        for (var request = 0; request < 100; request++)
        {
            // 两个目标交错入库，每个请求 50 条。
            var drafts = Enumerable.Range(0, 50).Select(index => Draft(connectionId: index % 2 == 0 ? 1 : 2)).ToList();
            expected.AddRange(drafts.Where(draft => draft.ConnectionId == 1).Select(draft => draft.DeliveryId));
            Assert.True((await writer.EnqueueAsync(drafts)).Committed);
        }

        var read = new List<StoredDelivery>();
        var cursor = 0L;
        while (true)
        {
            var page = writer.Cache.ReadPending(1, cursor, 100);
            if (page.Count == 0)
            {
                break;
            }

            read.AddRange(page);
            cursor = page[^1].Seq;

            // 发送者确认后删除：删除已读的记录不影响按游标继续读取。
            Assert.Equal(page.Count, (await writer.DeleteAsync([.. page.Select(record => record.Seq)])).AffectedRecords);
        }

        Assert.Equal(expected, read.Select(record => record.DeliveryId));
        Assert.True(read.Select(record => record.Seq).Order().SequenceEqual(read.Select(record => record.Seq)));
        Assert.Empty(writer.Cache.ReadPending(1, 0, 100));
        Assert.Equal(2500, ReadAll(writer.Cache, 2).Count);
    }

    [Fact]
    public async Task SeqsAreNotReusedAfterTheNewestRecordIsDeleted()
    {
        using var directory = TemporaryDirectory.Create();
        await using var writer = WriterHarness.Start(directory.Path);
        var first = Assert.Single((await writer.EnqueueAsync([Draft()])).Seqs);

        Assert.Equal(1, (await writer.DeleteAsync([first])).AffectedRecords);
        Assert.Equal(0, (await writer.DeleteAsync([first])).AffectedRecords);
        var second = Assert.Single((await writer.EnqueueAsync([Draft()])).Seqs);

        // 发送者按 seq 游标读取：被删除的最大 seq 若被复用，新记录会落在游标之前而被漏掉。
        Assert.True(second > first);
    }

    [Fact]
    public async Task ConcurrentRequestsAreCommittedInGroups()
    {
        using var directory = TemporaryDirectory.Create();
        await using var writer = WriterHarness.Start(directory.Path, options => WithInterval(options, TimeSpan.FromMilliseconds(100)));

        var results = await Task.WhenAll(Enumerable.Range(0, 200).Select(_ => writer.EnqueueAsync([Draft()])));

        Assert.All(results, result => Assert.True(result.Committed));
        var status = writer.Cache.GetStatus();
        Assert.Equal(200, status.CommittedRecords);
        Assert.True(status.CommittedBatches < 20, $"200 个请求用了 {status.CommittedBatches} 次提交。");
    }

    [Fact]
    public async Task AFullRequestQueueMakesProducersWait()
    {
        using var directory = TemporaryDirectory.Create();
        var cache = RuntimeTesting.CreateDeliveryCache(directory.Path, configure: options => new DeliveryCacheOptions
        {
            DatabasePath = options.DatabasePath,
            ConfigStoreId = options.ConfigStoreId,
            CommitInterval = options.CommitInterval,
            RequestQueueCapacity = 1,
        });

        // 写者没有运行：第一个请求占满队列并等待提交，第二个连交给写者都要等待。
        var first = cache.EnqueueAsync([Draft()], TestContext.Current.CancellationToken);
        using var giveUp = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        giveUp.CancelAfter(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.EnqueueAsync([Draft()], giveUp.Token));
        Assert.False(first.IsCompleted);

        await using var writer = WriterHarness.Attach(cache);
        Assert.True((await first.WaitAsync(Timeout, TestContext.Current.CancellationToken)).Committed);
        Assert.Single(cache.ReadPending(1, 0, 10));
    }

    [Fact]
    public async Task StoppingCommitsQueuedRequestsAndRejectsLaterOnes()
    {
        // 停机顺序（总设计 §8.1）：输入停止之后，写者提交请求队列中已有的请求（最后一次提交），之后到达的请求报告失败。
        using var directory = TemporaryDirectory.Create();
        await using var runtime = RuntimeTesting.CreateRuntime([], directory.Path, configureCache: options => WithInterval(options, TimeSpan.FromSeconds(30)));
        runtime.StartCore();
        var first = runtime.Deliveries.EnqueueAsync([Draft()], TestContext.Current.CancellationToken);
        await Task.Delay(200, TestContext.Current.CancellationToken);
        var queued = new[]
        {
            runtime.Deliveries.EnqueueAsync([Draft()], TestContext.Current.CancellationToken),
            runtime.Deliveries.EnqueueAsync([Draft(), Draft()], TestContext.Current.CancellationToken),
        };

        // 提交间隔是 30 秒：停止之前，这些请求都在等待。
        Assert.False(first.IsCompleted);
        await runtime.StopAsync(TestContext.Current.CancellationToken).WaitAsync(Timeout, TestContext.Current.CancellationToken);

        var results = await Task.WhenAll(queued.Prepend(first)).WaitAsync(Timeout, TestContext.Current.CancellationToken);
        Assert.All(results, result => Assert.True(result.Committed, result.Error));
        Assert.Equal(4, runtime.Deliveries.ReadPending(1, 0, 10).Count);
        var late = await runtime.Deliveries.EnqueueAsync([Draft()], TestContext.Current.CancellationToken).WaitAsync(Timeout, TestContext.Current.CancellationToken);
        Assert.False(late.Committed);
        Assert.Equal(DeliveryWriteStatus.Stopped, late.Status);
        Assert.Contains("已停止", late.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnavailableStoreFailsQueuedRequestsAndIsRetriedAfterBackoff()
    {
        using var directory = TemporaryDirectory.Create();
        var events = new RecordingEventSink();
        await using var runtime = RuntimeTesting.CreateRuntime([], directory.Path, events);

        // queue.db 的位置被一个目录占住：写者打不开发送缓存。
        var path = Path.Combine(directory.Path, DeliveryCacheOptions.FileName);
        Directory.CreateDirectory(path);
        var pending = runtime.Deliveries.EnqueueAsync([Draft()], TestContext.Current.CancellationToken);
        runtime.StartCore();

        var failed = await pending.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        Assert.False(failed.Committed);
        Assert.Contains("发送缓存不可用", failed.Error, StringComparison.Ordinal);
        var status = runtime.Deliveries.GetStatus();
        Assert.True(status.StorageFault);
        Assert.Contains("发送缓存不可用", status.LastError, StringComparison.Ordinal);
        Assert.Equal(1, status.FailedRecords);
        Assert.NotEmpty(events.OfType(RuntimeEventTypes.StorageFault));

        // 问题排除后，监督者按退避重启写者，之后的请求照常提交，状态恢复。
        Directory.Delete(path);
        var deadline = DateTime.UtcNow + Timeout;
        DeliveryWriteResult retried;
        do
        {
            retried = await runtime.Deliveries.EnqueueAsync([Draft()], TestContext.Current.CancellationToken).WaitAsync(Timeout, TestContext.Current.CancellationToken);
        }
        while (!retried.Committed && DateTime.UtcNow < deadline);

        Assert.True(retried.Committed, retried.Error);
        Assert.False(runtime.Deliveries.GetStatus().StorageFault);
        Assert.NotEmpty(events.OfType(RuntimeEventTypes.StorageRecovered));
        Assert.True(runtime.GetStatus().Units.Single(unit => unit.Id == DeliveryCache.WriterId).RestartCount >= 1);
    }

    [Fact]
    public async Task ABatchInFlightWhenTheStoreCannotBeReopenedIsCountedAsFailed()
    {
        // 写者初始化之后 queue.db 的位置被一个目录占住：取出本批后打开写连接失败，本批报告失败并计入失败记录数。
        using var directory = TemporaryDirectory.Create();
        await using var runtime = RuntimeTesting.CreateRuntime([], directory.Path, new RecordingEventSink());
        runtime.StartCore();
        var deadline = DateTime.UtcNow + Timeout;
        while (runtime.Deliveries.GetStatus().Store is null && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.NotNull(runtime.Deliveries.GetStatus().Store);
        var path = Path.Combine(directory.Path, DeliveryCacheOptions.FileName);
        File.Delete(path);
        Directory.CreateDirectory(path);

        var failed = await runtime.Deliveries.EnqueueAsync([Draft(), Draft(), Draft()], TestContext.Current.CancellationToken)
            .WaitAsync(Timeout, TestContext.Current.CancellationToken);

        Assert.False(failed.Committed);
        Assert.Contains("发送缓存不可用", failed.Error, StringComparison.Ordinal);
        var status = runtime.Deliveries.GetStatus();
        Assert.True(status.StorageFault);
        Assert.Equal(3, status.FailedRecords);
    }

    [Fact]
    public async Task ADatabaseMigratedByANewerProgramIsNotOpened()
    {
        using var directory = TemporaryDirectory.Create();
        var path = Path.Combine(directory.Path, DeliveryCacheOptions.FileName);
        using (var connection = Open(path))
        {
            Execute(connection, "PRAGMA user_version = 99;");
        }

        var cache = RuntimeTesting.CreateDeliveryCache(directory.Path);
        var writer = new DeliveryWriterUnit(cache, TimeProvider.System, new RecordingEventSink(), NullLogger.Instance);

        var pending = cache.EnqueueAsync([Draft()], TestContext.Current.CancellationToken);
        var exception = await Assert.ThrowsAsync<DeliveryStoreException>(() => writer.RunAsync(TestContext.Current.CancellationToken));
        Assert.Contains("不支持降级", exception.Message, StringComparison.Ordinal);

        // 打不开时不让等待的一方挂起：已排队的请求报告失败，状态为“存储异常”。
        var result = await pending.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        Assert.False(result.Committed);
        Assert.Contains("不支持降级", result.Error, StringComparison.Ordinal);
        Assert.True(cache.GetStatus().StorageFault);
    }

    [Fact]
    public async Task InvalidDraftsAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Draft(payload: new string('x', DeliveryDraft.MaximumPayloadBytes + 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Draft(payload: string.Empty));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DeliveryDraft(Guid.NewGuid(), 1, Guid.NewGuid(), "meter-001", 1, "{}"u8.ToArray(), "fp", "summary", qualityOfService: 2));
        Assert.Throws<ArgumentException>(() => Draft(deviceKey: "meter 001"));
        Assert.Throws<ArgumentOutOfRangeException>(() => Draft(connectionId: 0));

        var cache = RuntimeTesting.CreateDeliveryCache(Path.GetTempPath());
        var draft = Draft();
        await Assert.ThrowsAsync<ArgumentException>(() => cache.EnqueueAsync([draft, draft], TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => cache.EnqueueAsync([], TestContext.Current.CancellationToken));
    }

    private static DeliveryCacheOptions WithInterval(DeliveryCacheOptions options, TimeSpan interval) => new()
    {
        DatabasePath = options.DatabasePath,
        ConfigStoreId = options.ConfigStoreId,
        CommitInterval = interval,
    };

    private static DeliveryDraft Draft(
        long connectionId = 1,
        string deviceKey = "meter-001",
        string payload = """{"schemaVersion":1}""",
        IReadOnlyDictionary<string, string>? headers = null,
        long? routeId = null,
        string? nodeLabel = null) =>
        new(
            Guid.CreateVersion7(),
            connectionId,
            Guid.CreateVersion7(),
            deviceKey,
            revision: 1,
            Encoding.UTF8.GetBytes(payload),
            "fp-1",
            "mqtt://broker.local:1883 账户 gateway",
            "tenonedge/meter-001",
            qualityOfService: 1,
            headers,
            routeId,
            nodeLabel);

    private static List<StoredDelivery> ReadAll(DeliveryCache cache, long connectionId)
    {
        var all = new List<StoredDelivery>();
        var cursor = 0L;
        while (true)
        {
            var page = cache.ReadPending(connectionId, cursor, 1000);
            if (page.Count == 0)
            {
                return all;
            }

            all.AddRange(page);
            cursor = page[^1].Seq;
        }
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static void Execute(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>直接运行唯一的写者（不经监督者），停止时按停机顺序先停写者、再关闭请求队列。</summary>
    private sealed class WriterHarness : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stopping = new();
        private readonly Task _run;

        private WriterHarness(DeliveryCache cache)
        {
            Cache = cache;
            var writer = new DeliveryWriterUnit(cache, TimeProvider.System, Events, NullLogger.Instance);
            _run = Task.Run(() => writer.RunAsync(_stopping.Token));
        }

        public DeliveryCache Cache { get; }

        public RecordingEventSink Events { get; } = new();

        public static WriterHarness Start(string directory, Func<DeliveryCacheOptions, DeliveryCacheOptions>? configure = null) =>
            new(RuntimeTesting.CreateDeliveryCache(directory, configure: configure));

        public static WriterHarness Attach(DeliveryCache cache) => new(cache);

        /// <summary>入库并等待结果，最多等待 <see cref="Timeout"/>：写者没有在运行时用例立即失败，而不是挂起。</summary>
        public Task<DeliveryWriteResult> EnqueueAsync(IReadOnlyList<DeliveryDraft> drafts) =>
            Cache.EnqueueAsync(drafts, TestContext.Current.CancellationToken).WaitAsync(Timeout, TestContext.Current.CancellationToken);

        public Task<DeliveryWriteResult> DeleteAsync(IReadOnlyList<long> seqs) =>
            Cache.DeleteAsync(seqs, TestContext.Current.CancellationToken).WaitAsync(Timeout, TestContext.Current.CancellationToken);

        public async ValueTask DisposeAsync()
        {
            await _stopping.CancelAsync();
            await _run.WaitAsync(Timeout);
            Cache.CompleteAdding();
            Cache.FailRemaining("测试结束。");
            _stopping.Dispose();
        }
    }
}
