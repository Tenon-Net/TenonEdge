using Microsoft.Extensions.Logging.Abstractions;
using TenonEdge.Host.Features.Audit;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Storage;
using TenonEdge.Host.Storage.Ids;
using TenonEdge.IntegrationTests.Infrastructure;

namespace TenonEdge.IntegrationTests.Storage;

/// <summary>
/// 运行事件的最小存储与写入失败的回退（T0-09；总设计 §3.3）：
/// 同一对象的同类事件自第一次发生起 5 分钟内合并计数；审计与运行事件写不进配置库时改写到日志，不抛出，且输出条数有上限。
/// 写入失败以改名移走数据表来模拟，真实的磁盘写满未在自动化测试中复现。
/// </summary>
public sealed class RuntimeEventLogTests
{
    private const string Type = "connection.lost";

    private static readonly DateTimeOffset Start = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SameTypeTargetAndLevelMergeIntoOneEventWithACount()
    {
        using var store = TestConfigStore.Create();
        store.Initialize();
        var time = new ManualTimeProvider(Start);
        var events = CreateRuntimeEventLog(store, time, new LogCapture());

        events.Record(RuntimeEventLevel.Warning, Type, "plc-1", "第一次断开");
        time.Advance(TimeSpan.FromMinutes(2));
        events.Record(RuntimeEventLevel.Warning, Type, "plc-1", "第二次断开");
        events.Record(RuntimeEventLevel.Warning, Type, "plc-2", "另一个对象");
        events.Record(RuntimeEventLevel.Error, Type, "plc-1", "级别不同");
        time.Advance(TimeSpan.FromMinutes(1));
        events.Record(RuntimeEventLevel.Warning, Type, "plc-1", "第三次断开");
        events.Record(RuntimeEventLevel.Warning, "connection.restored", "plc-1", "类型不同");

        var records = Records(store);
        Assert.Equal(4, records.Count);
        var merged = records[0];
        Assert.Equal(("warning", "plc-1", 3, "第三次断开"), (merged.Level, merged.Target, merged.OccurrenceCount, merged.Message));
        Assert.Equal(Start.ToUnixTimeMilliseconds(), merged.FirstOccurredAt);
        Assert.Equal((Start + TimeSpan.FromMinutes(3)).ToUnixTimeMilliseconds(), merged.LastOccurredAt);
        Assert.Equal(["plc-2", "plc-1", "plc-1"], records.Skip(1).Select(record => record.Target));
        Assert.Equal(["warning", "error", "warning"], records.Skip(1).Select(record => record.Level));
        Assert.All(records.Skip(1), record => Assert.Equal(1, record.OccurrenceCount));
    }

    [Fact]
    public void TheMergeWindowIsCountedFromTheFirstOccurrence()
    {
        using var store = TestConfigStore.Create();
        store.Initialize();
        var time = new ManualTimeProvider(Start);
        var events = CreateRuntimeEventLog(store, time, new LogCapture());

        events.Record(RuntimeEventLevel.Warning, Type, "plc-1", "断开");
        time.SetUtcNow(Start + TimeSpan.FromMinutes(4));
        events.Record(RuntimeEventLevel.Warning, Type, "plc-1", "断开");
        time.SetUtcNow(Start + RuntimeEventLog.MergeWindow + TimeSpan.FromMilliseconds(1));
        events.Record(RuntimeEventLevel.Warning, Type, "plc-1", "断开");

        // 距上一次只有 1 分钟，但距第一次已超过 5 分钟：另起一条，计数反映“5 分钟内断开几次”。
        Assert.Equal([2, 1], Records(store).Select(record => record.OccurrenceCount));
    }

    [Fact]
    public void RuntimeEventWriteFailuresAreLoggedWithinTheLimitInsteadOfThrowing()
    {
        using var store = TestConfigStore.Create();
        store.Initialize();
        var time = new ManualTimeProvider(Start);
        var capture = new LogCapture();
        var events = CreateRuntimeEventLog(store, time, capture);
        store.Database.Client.Ado.ExecuteCommand($"ALTER TABLE {RuntimeEventRecord.TableName} RENAME TO runtime_event_unavailable");

        for (var i = 0; i < 50; i++)
        {
            events.Record(RuntimeEventLevel.Warning, Type, "plc-1", $"第 {i} 次断开");
        }

        var written = capture.Matching(typeof(RuntimeEventLog).FullName!, "运行事件写入配置库失败，改写到日志");
        Assert.Equal(StorageWriteFallback.LinesPerMinute, written.Count);
        Assert.Contains("类型 connection.lost，对象 plc-1，说明 第 0 次断开", written[0].Message, StringComparison.Ordinal);
        Assert.Single(capture.Matching(typeof(StorageWriteFallback).FullName!, "每分钟 20 条的上限"));

        time.Advance(TimeSpan.FromMinutes(1));
        events.Record(RuntimeEventLevel.Warning, Type, "plc-1", "下一分钟");

        Assert.Single(capture.Matching(typeof(StorageWriteFallback).FullName!, "另有 30 条运行事件写入配置库失败"));
        Assert.Equal(StorageWriteFallback.LinesPerMinute + 1, capture.Matching(typeof(RuntimeEventLog).FullName!, "改写到日志").Count);
    }

    [Fact]
    public void AuditWriteFailuresOutsideATransactionAreLoggedWithinTheLimit()
    {
        using var store = TestConfigStore.Create();
        store.Initialize();
        var time = new ManualTimeProvider(Start);
        var capture = new LogCapture();
        var fallback = new StorageWriteFallback(time, capture.CreateLogger<StorageWriteFallback>());
        var audit = new AuditTrail(store.Database, CreateIds(store, time), time, fallback, capture.CreateLogger<AuditTrail>());
        store.Database.Client.Ado.ExecuteCommand($"ALTER TABLE {AuditLogRecord.TableName} RENAME TO audit_log_unavailable");

        for (var i = 0; i < 30; i++)
        {
            audit.AppendOrLog(new AuditEntry(AuditActions.Login, AuditResults.Failure, "admin", "192.0.2.10"));
        }

        var written = capture.Matching(typeof(AuditTrail).FullName!, "审计记录写入配置库失败，改写到日志");
        Assert.Equal(StorageWriteFallback.LinesPerMinute, written.Count);
        Assert.Contains("操作 auth.login，结果 failure，账号 admin，来源 192.0.2.10", written[0].Message, StringComparison.Ordinal);
    }

    private static List<RuntimeEventRecord> Records(TestConfigStore store) =>
        store.Database.Client.Queryable<RuntimeEventRecord>().OrderBy(record => record.Id).ToList();

    private static SnowflakeIdGenerator CreateIds(TestConfigStore store, TimeProvider time) =>
        IdGeneration.CreateGenerator(store.Database, time, NullLogger.Instance);

    private static RuntimeEventLog CreateRuntimeEventLog(TestConfigStore store, TimeProvider time, LogCapture capture) =>
        new(
            store.Database,
            CreateIds(store, time),
            time,
            new StorageWriteFallback(time, capture.CreateLogger<StorageWriteFallback>()),
            capture.CreateLogger<RuntimeEventLog>());
}
