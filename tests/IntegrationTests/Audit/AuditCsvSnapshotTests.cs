using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TenonEdge.Host.Features.Audit;
using TenonEdge.Host.Storage;
using TenonEdge.Host.Storage.Ids;
using TenonEdge.IntegrationTests.Infrastructure;

namespace TenonEdge.IntegrationTests.Audit;

public sealed class AuditCsvSnapshotTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task CsvUsesTheCompleteFilteredInventoryAcrossShortPagesAndExcludesLaterWrites()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var database = factory.Services.GetRequiredService<ConfigDatabase>();
        var ids = factory.Services.GetRequiredService<SnowflakeIdGenerator>();
        var now = DateTimeOffset.UtcNow;
        var rows = Enumerable.Range(0, 405).Select(i => new AuditLogRecord
        {
            Id = ids.NextId(),
            OccurredAt = now.ToUnixTimeMilliseconds(),
            AccountName = "account",
            TokenName = "MES",
            SourceIp = "192.0.2.1",
            Action = "test.csv",
            Target = "object",
            Result = "success",
            Detail = "row-" + i,
        }).ToArray();
        database.InTransaction(client => client.Insertable(rows).ExecuteCommand());
        factory.Services.GetRequiredService<AuditTrail>().AppendOrLog(new("test.csv", "success", "another", null, "object"));
        var filter = new AuditFilter(now.AddSeconds(-1), now.AddSeconds(1), "account", "test.csv", "object");
        using var snapshot = AuditCsvSnapshot.Capture(database, factory.Services.GetRequiredService<AuditRetentionGate>(), filter);
        factory.Services.GetRequiredService<AuditTrail>().AppendOrLog(new("test.csv", "success", "account", null, "object", "later"));
        using var stream = new MemoryStream();
        Assert.Equal(405, await snapshot.WriteAsync(stream, Token));
        Assert.Equal(new byte[] { 0xef, 0xbb, 0xbf }, stream.ToArray()[..3]);
        var csv = Encoding.UTF8.GetString(stream.ToArray());
        Assert.Contains("\"MES\",\"192.0.2.1\",\"test.csv\",\"object\",\"success\",\"row-404\"\r\n", csv, StringComparison.Ordinal);
        Assert.EndsWith("\"row-0\"\r\n", csv, StringComparison.Ordinal);
        Assert.DoesNotContain("later", csv, StringComparison.Ordinal);
        Assert.DoesNotContain("another", csv, StringComparison.Ordinal);
        Assert.Equal(406, csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length);
        await Assert.ThrowsAsync<InvalidOperationException>(() => snapshot.WriteAsync(stream, Token));
    }

    [Fact]
    public async Task CsvQuotesEmbeddedSeparatorsAndNewlinesAndNeutralizesSpreadsheetFormulaPrefixes()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        factory.Services.GetRequiredService<AuditTrail>().AppendOrLog(new("test.csv", "success", new AuditAccount(" =1+1", "@MES"),
            "192.0.2.1", "quoted,\"对象\"", "\t=SUM(1,2)\r\nnext"));
        using var snapshot = AuditCsvSnapshot.Capture(factory.Services.GetRequiredService<ConfigDatabase>(),
            factory.Services.GetRequiredService<AuditRetentionGate>(), new(Action: "test.csv"));
        using var stream = new MemoryStream();
        Assert.Equal(1, await snapshot.WriteAsync(stream, Token));
        var csv = Encoding.UTF8.GetString(stream.ToArray());
        Assert.Contains("\"' =1+1\",\"'@MES\",\"192.0.2.1\",\"test.csv\",\"quoted,\"\"对象\"\"\",\"success\",\"'\t=SUM(1,2)\r\nnext\"\r\n", csv, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PausedStandaloneAuditCannotInsertBelowAnAlreadyCapturedExportFence()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var database = factory.Services.GetRequiredService<ConfigDatabase>();
        var normal = factory.Services.GetRequiredService<AuditTrail>();
        normal.AppendOrLog(new("test.csv", "success", "before", null));
        using var clock = new GatedClock();
        var delayed = new AuditTrail(database, factory.Services.GetRequiredService<SnowflakeIdGenerator>(), clock,
            factory.Services.GetRequiredService<StorageWriteFallback>(), NullLogger<AuditTrail>.Instance);
        var pending = Task.Run(() => delayed.AppendOrLog(new("test.csv", "success", "late", null)), Token);
        try
        {
            await clock.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            normal.AppendOrLog(new("test.csv", "success", "fence", null));
            using var snapshot = AuditCsvSnapshot.Capture(database, factory.Services.GetRequiredService<AuditRetentionGate>(), new(Action: "test.csv"));
            clock.Release.Set();
            await pending.WaitAsync(TimeSpan.FromSeconds(10), Token);
            using var stream = new MemoryStream();
            Assert.Equal(2, await snapshot.WriteAsync(stream, Token));
            Assert.DoesNotContain("late", Encoding.UTF8.GetString(stream.ToArray()), StringComparison.Ordinal);
            Assert.True(database.Client.Queryable<AuditLogRecord>().Single(row => row.AccountName == "late").Id > snapshot.UpperId);
        }
        finally { clock.Release.Set(); await pending.WaitAsync(TimeSpan.FromSeconds(10), Token); }
    }

    [Fact]
    public async Task SlowCsvOutputDoesNotHoldADatabaseTransactionAndOnlyDefersAuditRetention()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var database = factory.Services.GetRequiredService<ConfigDatabase>();
        var gate = factory.Services.GetRequiredService<AuditRetentionGate>();
        var now = DateTimeOffset.UtcNow;
        database.InTransaction(client =>
        {
            client.Insertable(new AuditLogRecord { Id = 1, AccountName = "old", Action = "test.csv", Result = "success", OccurredAt = now.AddDays(-200).ToUnixTimeMilliseconds() }).ExecuteCommand();
            client.Insertable(new RuntimeEventRecord { Id = 1, Level = "info", Type = "test", Target = "old", Message = "old", FirstOccurredAt = now.AddDays(-8).ToUnixTimeMilliseconds(), LastOccurredAt = now.AddDays(-8).ToUnixTimeMilliseconds(), OccurrenceCount = 1 }).ExecuteCommand();
            return true;
        });
        var cleanup = new DataRetentionService(database, new(TimeSpan.FromDays(180)), TimeProvider.System,
            new TestClockSyncStatus(), NullLogger<DataRetentionService>.Instance, gate);
        var writes = 0;
        using (var snapshot = AuditCsvSnapshot.Capture(database, gate, new(Action: "test.csv")))
        {
            using var output = new ProbedStream(() =>
            {
                if (Interlocked.Increment(ref writes) != 1)
                { return; }
                // While output is awaiting, a separate connection can acquire/commit the normal write transaction.
                factory.Services.GetRequiredService<AuditTrail>().AppendOrLog(new("test.later", "success", "writer", null));
                Assert.Equal((0, 1), cleanup.PurgeExpired());
                Assert.True(database.Client.Queryable<AuditLogRecord>().Any(row => row.Id == 1));
            });
            Assert.Equal(1, await snapshot.WriteAsync(output, Token));
            Assert.Throws<AuditSnapshotBusyException>(() => AuditCsvSnapshot.Capture(database, gate, new()));
        }
        Assert.True(writes > 0);
        Assert.True(gate.PurgePending);
        Assert.Throws<AuditSnapshotBusyException>(() => AuditCsvSnapshot.Capture(database, gate, new()));
        Assert.Equal((1, 0), cleanup.PurgeExpired());
        using var next = AuditCsvSnapshot.Capture(database, gate, new(Action: "test.csv"));
        using var empty = new MemoryStream();
        Assert.Equal(0, await next.WriteAsync(empty, Token));
    }

    private sealed class GatedClock : TimeProvider, IDisposable
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new();
        public override DateTimeOffset GetUtcNow()
        {
            Entered.TrySetResult();
            if (!Release.Wait(TimeSpan.FromSeconds(10)))
            { throw new TimeoutException("audit timestamp probe not released"); }
            return DateTimeOffset.UtcNow;
        }
        public void Dispose() => Release.Dispose();
    }
    private sealed class ProbedStream(Action write) : MemoryStream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        { write(); return base.WriteAsync(buffer, cancellationToken); }
    }
}
