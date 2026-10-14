using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Host.Features.Audit;
using TenonEdge.Host.Storage;
using TenonEdge.IntegrationTests.Infrastructure;

namespace TenonEdge.IntegrationTests.Storage;

/// <summary>
/// 审计与运行事件的保留清理（T0-09；总设计 §3.3，功能需求 §9.2、§9.4）：审计超过保留期（默认 180 天，可调长）自动清理；
/// 运行事件按最近一次发生保留 7 天，最多 1 万条，超出时先删最旧的；宿主启动后即执行一次清理。
/// 只有保存的时间与 ID 中的逻辑时间戳相符（写入时时钟不落后）的记录才按保留期删除；写入时时钟落后的记录真实时间未知，保留（总设计 §10.1）。
/// </summary>
public sealed class DataRetentionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task DeferredPurgeHasPriorityAndBackgroundRetryDoesNotWaitAnotherHour()
    {
        using var store = TestConfigStore.Create();
        store.Initialize();
        InsertAudit(store, Now.AddDays(-200));
        var time = new FakeTimeProvider(Now);
        var gate = new AuditRetentionGate();
        using var snapshot = gate.TryEnterSnapshot();
        Assert.NotNull(snapshot);
        using var service = new DataRetentionService(store.Database, new(TimeSpan.FromDays(180)), time,
            new TestClockSyncStatus(), NullLogger<DataRetentionService>.Instance, gate);
        var token = TestContext.Current.CancellationToken;
        await service.StartAsync(token);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            while (!gate.PurgePending)
            { await Task.Delay(10, timeout.Token); }
            Assert.Equal(1, store.Database.Client.Queryable<AuditLogRecord>().Count());
            snapshot.Dispose();
            Assert.Null(gate.TryEnterSnapshot());
            // The first delay may be registered just after pending becomes visible; drive several short retries.
            while (gate.PurgePending)
            { time.Advance(DataRetentionService.DeferredAuditRetryInterval); await Task.Delay(10, timeout.Token); }
            while (store.Database.Client.Queryable<AuditLogRecord>().Any())
            { await Task.Delay(10, timeout.Token); }
            using var next = gate.TryEnterSnapshot();
            Assert.NotNull(next);
        }
        finally { await service.StopAsync(timeout.Token); }
    }

    [Theory]
    [InlineData(ClockSyncState.Unknown)]
    [InlineData(ClockSyncState.NotSynchronized)]
    public void UntrustedClockKeepsExpiredRecordsUntilSynchronizationReturns(ClockSyncState state)
    {
        using var store = TestConfigStore.Create();
        store.Initialize();
        InsertAudit(store, Now.AddDays(-200));
        InsertEvents(store, [(Now.AddDays(-8), Now.AddDays(-8))]);
        var clock = new TestClockSyncStatus { State = state };
        var service = new DataRetentionService(store.Database, new(TimeSpan.FromDays(180)),
            new ManualTimeProvider(Now), clock, NullLogger<DataRetentionService>.Instance, new AuditRetentionGate());

        Assert.Equal((0, 0), service.PurgeExpired());
        Assert.Equal(1, store.Database.Client.Queryable<AuditLogRecord>().Count());
        Assert.Equal(1, store.Database.Client.Queryable<RuntimeEventRecord>().Count());
        clock.State = ClockSyncState.Synchronized;
        Assert.Equal((1, 1), service.PurgeExpired());
    }

    [Theory]
    [InlineData(400)]
    [InlineData(-400)]
    public void WallClockJumpDoesNotDeleteUntilItReturnsToMonotonicTimeline(int days)
    {
        using var store = TestConfigStore.Create();
        store.Initialize();
        InsertAudit(store, Now.AddDays(-179));
        InsertEvents(store, [(Now.AddDays(-6), Now.AddDays(-6))]);
        var time = new ManualTimeProvider(Now);
        var service = new DataRetentionService(store.Database, new(TimeSpan.FromDays(180)), time,
            new TestClockSyncStatus(), NullLogger<DataRetentionService>.Instance, new AuditRetentionGate());
        time.StepWallClock(TimeSpan.FromDays(days));
        time.Advance(TimeSpan.FromDays(3));

        Assert.Equal((0, 0), service.PurgeExpired());
        // 重复调用不能把异常墙上时间重新当作可信基准。
        Assert.Equal((0, 0), service.PurgeExpired());
        Assert.Equal(1, store.Database.Client.Queryable<AuditLogRecord>().Count());
        Assert.Equal(1, store.Database.Client.Queryable<RuntimeEventRecord>().Count());
        time.StepWallClock(TimeSpan.FromDays(-days));
        Assert.Equal((1, 1), service.PurgeExpired());
    }

    [Fact]
    public void UntrustedClockStillEnforcesEventCountLimitButNeverPrunesAuditByCount()
    {
        using var store = TestConfigStore.Create();
        store.Initialize();
        InsertAudit(store, Now.AddDays(-400));
        InsertEvents(store, [.. Enumerable.Range(0, DataRetentionSettings.MaximumRuntimeEvents + 1)
            .Select(_ => (Now.AddDays(-8), Now.AddDays(-8)))]);
        var service = new DataRetentionService(store.Database, new(TimeSpan.FromDays(180)), new ManualTimeProvider(Now),
            new TestClockSyncStatus { State = ClockSyncState.Unknown }, NullLogger<DataRetentionService>.Instance, new AuditRetentionGate());

        Assert.Equal((0, 1), service.PurgeExpired());
        Assert.Equal(1, store.Database.Client.Queryable<AuditLogRecord>().Count());
        Assert.Equal(DataRetentionSettings.MaximumRuntimeEvents, store.Database.Client.Queryable<RuntimeEventRecord>().Count());
    }

    [Fact]
    public void AuditRecordsOlderThanTheDefaultRetentionAreDeleted()
    {
        using var store = TestConfigStore.Create();
        store.Initialize();
        InsertAudit(store, Now.AddDays(-181), Now.AddDays(-180).AddMilliseconds(-1), Now.AddDays(-180), Now.AddDays(-179), Now);

        var (audit, events) = CreateService(store, TimeSpan.FromDays(180)).PurgeExpired();

        Assert.Equal((2, 0), (audit, events));
        Assert.Equal(
            [Now.AddDays(-180), Now.AddDays(-179), Now],
            store.Database.Client.Queryable<AuditLogRecord>().OrderBy(record => record.Id).ToList()
                .Select(record => DateTimeOffset.FromUnixTimeMilliseconds(record.OccurredAt)));
    }

    [Fact]
    public void LongerAuditRetentionKeepsOlderRecords()
    {
        using var store = TestConfigStore.Create();
        store.Initialize();
        InsertAudit(store, Now.AddDays(-366), Now.AddDays(-200));

        var (audit, _) = CreateService(store, TimeSpan.FromDays(365)).PurgeExpired();

        Assert.Equal(1, audit);
        Assert.Equal(Now.AddDays(-200).ToUnixTimeMilliseconds(), store.Database.Client.Queryable<AuditLogRecord>().Single().OccurredAt);
    }

    [Fact]
    public void RuntimeEventsAreDeletedSevenDaysAfterTheirLastOccurrence()
    {
        using var store = TestConfigStore.Create();
        store.Initialize();
        InsertEvents(store, [
            (Now.AddDays(-9), Now.AddDays(-8)),
            (Now.AddDays(-10), Now.AddDays(-6)),
            (Now.AddHours(-1), Now.AddHours(-1)),
        ]);

        var (_, events) = CreateService(store, TimeSpan.FromDays(180)).PurgeExpired();

        Assert.Equal(1, events);
        Assert.Equal(
            [Now.AddDays(-6).ToUnixTimeMilliseconds(), Now.AddHours(-1).ToUnixTimeMilliseconds()],
            store.Database.Client.Queryable<RuntimeEventRecord>().OrderBy(record => record.Id).ToList().Select(record => record.LastOccurredAt));
    }

    [Fact]
    public void OnlyTheNewestTenThousandRuntimeEventsAreKept()
    {
        using var store = TestConfigStore.Create();
        store.Initialize();
        const int total = DataRetentionSettings.MaximumRuntimeEvents + 2_345;
        InsertEvents(store, [.. Enumerable.Range(0, total).Select(_ => (Now.AddMinutes(-10), Now.AddMinutes(-10)))]);

        var (_, events) = CreateService(store, TimeSpan.FromDays(180)).PurgeExpired();

        Assert.Equal(2_345, events);
        var remaining = store.Database.Client.Queryable<RuntimeEventRecord>().Select(record => record.Id).ToList();
        Assert.Equal(DataRetentionSettings.MaximumRuntimeEvents, remaining.Count);
        Assert.Equal(2_346, remaining.Min());
        Assert.Equal(total, remaining.Max());
    }

    [Fact]
    public void RecordsWrittenWhileTheClockWasBehindAreKeptBecauseTheirAgeIsUnknown()
    {
        using var store = TestConfigStore.Create();
        store.Initialize();

        // 没有实时时钟的设备在构建之后很久才以 1970 年的时钟完成设置：保存的时间为 0，
        // ID 的逻辑时间戳只是真实时间的下限（这里停在 200 天前），两者都说明不了记录有多旧。
        var writtenWhileBehind = (Id: TestIds.At(Now.AddDays(-200), lowBits: 1), Time: DateTimeOffset.UnixEpoch);
        var old = (Id: TestIds.At(Now.AddDays(-200), lowBits: 2), Time: Now.AddDays(-200));
        var recent = (Id: TestIds.At(Now.AddDays(-3), lowBits: 1), Time: Now.AddDays(-3));
        store.Database.InTransaction(client =>
        {
            foreach (var (id, time) in new[] { writtenWhileBehind, old, recent })
            {
                var milliseconds = time.ToUnixTimeMilliseconds();
                client.Insertable(new AuditLogRecord { Id = id, OccurredAt = milliseconds, Action = AuditActions.Login, Result = AuditResults.Success }).ExecuteCommand();
                client.Insertable(new RuntimeEventRecord
                {
                    Id = id,
                    FirstOccurredAt = milliseconds,
                    LastOccurredAt = milliseconds,
                    Level = "info",
                    Type = "test.event",
                    Target = $"object-{id}",
                    Message = "测试事件",
                }).ExecuteCommand();
            }

            return true;
        });

        var (audit, events) = CreateService(store, TimeSpan.FromDays(180)).PurgeExpired();

        Assert.Equal((1, 1), (audit, events));
        Assert.Equal(
            [writtenWhileBehind.Id, recent.Id],
            store.Database.Client.Queryable<AuditLogRecord>().OrderBy(record => record.Id).Select(record => record.Id).ToList());
        Assert.Equal(
            [writtenWhileBehind.Id, recent.Id],
            store.Database.Client.Queryable<RuntimeEventRecord>().OrderBy(record => record.Id).Select(record => record.Id).ToList());
    }

    [Fact]
    public void AClockStillBehindTheIdEpochDeletesNothing()
    {
        using var store = TestConfigStore.Create();
        store.Initialize();
        var epochStart = DateTimeOffset.UnixEpoch;
        InsertAudit(store, epochStart, epochStart.AddDays(1));
        InsertEvents(store, [(epochStart, epochStart)]);
        var service = new DataRetentionService(
            store.Database,
            new DataRetentionSettings(TimeSpan.FromDays(DataRetentionSettings.MinimumAuditRetentionDays)),
            new ManualTimeProvider(new DateTimeOffset(1971, 1, 1, 0, 0, 0, TimeSpan.Zero)),
            new TestClockSyncStatus(),
            NullLogger<DataRetentionService>.Instance, new AuditRetentionGate());

        // 按保存的时间，这些记录都已超过保留期；但时钟读数早于 ID 的纪元（2020 年），据此无法判断任何记录已过期。
        Assert.Equal((0, 0), service.PurgeExpired());
    }

    [Fact]
    public async Task ExpiredRecordsArePurgedWhenTheServiceStarts()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var dataDirectory = TemporaryDirectory.Create();
        var now = DateTimeOffset.UtcNow;
        using (var store = TestConfigStore.Open(dataDirectory.Path))
        {
            store.Initialize();
            InsertAudit(store, now.AddDays(-400), now.AddDays(-1));
            InsertEvents(store, [(now.AddDays(-30), now.AddDays(-30))]);
        }

        await using var factory = new FixedDataDirectoryApplicationFactory(dataDirectory.Path,
            configureServices: services => services.AddSingleton<IClockSyncStatus>(new TestClockSyncStatus()));
        var database = factory.Services.GetRequiredService<ConfigDatabase>();

        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (database.Client.Queryable<AuditLogRecord>().Count() > 1
            || database.Client.Queryable<RuntimeEventRecord>().Any(record => record.LastOccurredAt < now.AddDays(-7).ToUnixTimeMilliseconds()))
        {
            Assert.True(DateTimeOffset.UtcNow < deadline, "启动后 15 秒内没有完成保留期清理。");
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        }

        Assert.Equal(now.AddDays(-1).ToUnixTimeMilliseconds(), database.Client.Queryable<AuditLogRecord>().Single().OccurredAt);
    }

    private static DataRetentionService CreateService(TestConfigStore store, TimeSpan auditRetention) =>
        new(store.Database, new DataRetentionSettings(auditRetention), new ManualTimeProvider(Now),
            new TestClockSyncStatus(), NullLogger<DataRetentionService>.Instance, new AuditRetentionGate());

    private static void InsertAudit(TestConfigStore store, params DateTimeOffset[] occurredAt)
    {
        var records = occurredAt.Select((time, index) => new AuditLogRecord
        {
            Id = index + 1,
            OccurredAt = time.ToUnixTimeMilliseconds(),
            AccountName = "admin",
            Action = AuditActions.Login,
            Result = AuditResults.Success,
        }).ToList();
        store.Database.InTransaction(client => client.Insertable(records).ExecuteCommand());
    }

    private static void InsertEvents(TestConfigStore store, IReadOnlyList<(DateTimeOffset First, DateTimeOffset Last)> occurrences)
    {
        var records = occurrences.Select((occurrence, index) => new RuntimeEventRecord
        {
            Id = index + 1,
            FirstOccurredAt = occurrence.First.ToUnixTimeMilliseconds(),
            LastOccurredAt = occurrence.Last.ToUnixTimeMilliseconds(),
            Level = "info",
            Type = "test.event",
            Target = $"object-{index}",
            Message = "测试事件",
        }).ToList();
        store.Database.InTransaction(client => client.Insertable(records).ExecuteCommand());
    }
}
