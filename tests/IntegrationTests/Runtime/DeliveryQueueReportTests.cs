using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.Runtime.Deliveries;

namespace TenonEdge.IntegrationTests.Runtime;

public sealed class DeliveryQueueReportTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static DeliveryDraft Draft(long connection) => new(Guid.NewGuid(), connection, Guid.NewGuid(), "meter", 1,
        Encoding.UTF8.GetBytes("{\"private\":\"report-must-not-read-body\"}"), "fingerprint", "old-recipient", "topic");

    [Fact]
    public async Task ReportsActualCommittedStatesBytesReasonsAndCountersWithoutBodies()
    {
        using var data = TemporaryDirectory.Create();
        await using var runtime = RuntimeTesting.CreateRuntime([], data.Path);
        runtime.StartCore();
        var cache = runtime.Deliveries;
        var drafts = new[] { Draft(1), Draft(1), Draft(1), Draft(2), Draft(2) };
        var write = await cache.EnqueueAsync(drafts, Token);
        Assert.True(write.Committed);
        Assert.True((await cache.RecordFailureAsync(new(write.Seqs[1], 0, "fixture-rejection", true, false), Token)).Committed);
        Assert.True((await cache.QuarantineAsync(write.Seqs[2], DeliveryQuarantineReasons.TargetChanged, Token)).Committed);
        Assert.True((await cache.QuarantineAsync(write.Seqs[3], DeliveryQuarantineReasons.TargetMissing, Token)).Committed);
        Assert.True((await cache.QuarantineAsync(write.Seqs[4], DeliveryQuarantineReasons.ConfigStoreChanged, Token)).Committed);
        var report = cache.GetQueueReport([1, 2, 3]);
        Assert.Equal(1, report.Usage.PendingCount);
        Assert.Equal(1, report.Usage.FailedCount);
        Assert.Equal(3, report.Usage.QuarantinedCount);
        Assert.Equal(drafts[0].ByteSize, report.Usage.PendingBytes);
        Assert.Equal(drafts[1].ByteSize, report.Usage.FailedBytes);
        Assert.Equal(drafts.Skip(2).Sum(draft => draft.ByteSize), report.Usage.QuarantinedBytes);
        Assert.Equal(1, report.Usage.TargetChangedCount);
        Assert.Equal(1, report.Usage.TargetMissingCount);
        Assert.Equal(1, report.Usage.ConfigStoreChangedCount);
        Assert.Equal(0, report.Usage.UnknownQuarantineCount);
        Assert.Equal(3, report.Counters.Quarantined);
        Assert.Equal(1, report.Counters.Rejected);
        Assert.Equal(write.Seqs[0], report.Targets[0].HeadSequence);
        Assert.NotNull(report.Targets[0].HeadCreatedAt);
        Assert.Null(report.Targets[1].HeadSequence);
        Assert.Equal(0, report.Targets[2].Usage.PendingCount);
        Assert.NotNull(report.Files);
        Assert.True(report.Files.DatabaseBytes > 0);
        var json = JsonSerializer.Serialize(report);
        Assert.DoesNotContain("report-must-not-read-body", json, StringComparison.Ordinal);
        Assert.DoesNotContain("old-recipient", json, StringComparison.Ordinal);
        Assert.DoesNotContain("fingerprint", json, StringComparison.Ordinal);
        await runtime.StopAsync(Token);
        var reopened = RuntimeTesting.CreateDeliveryCache(data.Path);
        var after = reopened.GetQueueReport([3]);
        Assert.Equal(report.Usage, after.Usage);
        Assert.Equal(report.Counters, after.Counters);
        reopened.Store.ClearReadPool();
    }

    [Fact]
    public async Task PagesAcrossActiveEmptyHistoricalAndOrphanTargetsWithoutDoubleCounting()
    {
        using var data = TemporaryDirectory.Create();
        await using var runtime = RuntimeTesting.CreateRuntime([], data.Path);
        runtime.StartCore();
        var cache = runtime.Deliveries;
        var result = await cache.EnqueueAsync([Draft(2), Draft(4)], Token);
        await cache.RecordFailureAsync(new(result.Seqs[0], 0, "rejected", true, false), Token);
        await cache.DeleteAsync([result.Seqs[0]], Token);
        var maximum = cache.GetQueueReport(Enumerable.Range(1, 500).Select(id => (long)id).ToArray(), 400);
        Assert.Equal(100, maximum.Targets.Length);
        Assert.Equal(500, maximum.Targets[^1].ConnectionId);
        Assert.Null(maximum.NextConnectionId);
        var active = new long[] { 1, 3, 5, 5 };
        var page = cache.GetQueueReport(active, limit: 2);
        Assert.Equal([1L, 2L], page.Targets.Select(target => target.ConnectionId));
        Assert.Equal(2, page.NextConnectionId);
        Assert.Equal(0, page.Targets[1].Usage.FailedCount);
        Assert.Equal(1, page.Targets[1].Counters.Rejected);
        var next = cache.GetQueueReport(active, page.NextConnectionId!.Value, 2);
        Assert.Equal([3L, 4L], next.Targets.Select(target => target.ConnectionId));
        var last = cache.GetQueueReport(active, next.NextConnectionId!.Value, 2);
        Assert.Equal(5, Assert.Single(last.Targets).ConnectionId);
        Assert.Null(last.NextConnectionId);
        Assert.Equal(page.Usage, last.Usage);
        Assert.Equal(1, last.Usage.PendingCount);
        Assert.Throws<ArgumentException>(() => cache.GetQueueReport([0]));
        Assert.Throws<ArgumentException>(() => cache.GetQueueReport([], limit: 0));
        Assert.Throws<ArgumentException>(() => cache.GetQueueReport([], limit: 101));
        Assert.Throws<ArgumentException>(() => cache.GetQueueReport([], after: -1));
        Assert.Throws<ArgumentException>(() => cache.GetQueueReport(Enumerable.Range(1, 501).Select(id => (long)id).ToArray()));
    }

    [Fact]
    public async Task ReadSnapshotDoesNotSeeUncommittedChangesAndReportsUnknownReasonsAndClockAnomaly()
    {
        using var data = TemporaryDirectory.Create();
        await using var runtime = RuntimeTesting.CreateRuntime([], data.Path);
        runtime.StartCore();
        var cache = runtime.Deliveries;
        var result = await cache.EnqueueAsync([Draft(1)], Token);
        using var writer = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = cache.Options.DatabasePath, Pooling = false }.ToString());
        writer.Open();
        using var tx = writer.BeginTransaction();
        using var command = writer.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "UPDATE delivery SET state = 'quarantined', quarantine_reason = 'fixture-unknown', clock_anomaly = 1";
        command.ExecuteNonQuery();
        var before = cache.GetQueueReport([]);
        Assert.Equal(1, before.Usage.PendingCount);
        Assert.Equal(result.Seqs[0], before.Targets[0].HeadSequence);
        Assert.Equal(0, before.Usage.ClockAnomalyCount);
        tx.Commit();
        var after = cache.GetQueueReport([]);
        Assert.Equal(0, after.Usage.PendingCount);
        Assert.Equal(1, after.Usage.UnknownQuarantineCount);
        Assert.Equal(1, after.Usage.ClockAnomalyCount);
        Assert.Null(after.Targets[0].HeadSequence);
    }
}
