using Microsoft.Data.Sqlite;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.Runtime.Deliveries;

namespace TenonEdge.IntegrationTests.Runtime;

public sealed class DeliveryMigrationPreflightTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static DeliveryDraft Draft(long connection = 1) => new(Guid.NewGuid(), connection, Guid.NewGuid(), "meter", 1,
        "{}"u8.ToArray(), "old-fingerprint", "http://old.test/; authentication=none", "events");
    private static DeliveryMigrationKey Key(StoredDelivery row) => new(row.Seq, row.DeliveryId, row.MigrationId);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadOnlyPreflightAndFinalWriterReportTheSameCompleteQuotaDeficit(bool failed)
    {
        using var data = TemporaryDirectory.Create();
        await using var runtime = RuntimeTesting.CreateRuntime([], data.Path, configureCache: options => new()
        { DatabasePath = options.DatabasePath, ConfigStoreId = options.ConfigStoreId, CommitInterval = options.CommitInterval, MaximumPendingPerTarget = 2 });
        runtime.StartCore();
        var cache = runtime.Deliveries;
        var seqs = (await cache.EnqueueAsync([Draft(), Draft(), Draft(2)], Token)).Seqs;
        if (failed)
        { await cache.RecordFailureAsync(new(seqs[0], 1, "rejected", true, false), Token); }
        var records = cache.ReadPending(1, 0, 10).Concat(cache.ReadFailed(1, 0, 10)).OrderBy(row => row.Seq).ToArray();
        var candidates = cache.ReadMigrationCandidates(1, records.Select(Key).ToArray());
        var command = DeliveryMigrationCommand.Selected(cache.Options.ConfigStoreId, Guid.NewGuid(), 1, 2,
            "new-fingerprint", "http://target.test/; authentication=none", candidates.Select(candidate => candidate.Item).ToArray());
        var counters = cache.GetPersistentCounters();
        var before = cache.GetQueueTotals(1);
        var preflight = cache.PreflightMigration(command);
        Assert.Equal(DeliveryMigrationErrors.QuotaExceeded, preflight.Error);
        Assert.Equal(2, preflight.Count);
        Assert.Equal(1, preflight.Quota!.PendingRecords); // Failed records must also fit the destination pending capacity.
        Assert.Null(cache.ReadMigrationReceipt(command.OperationId));
        Assert.Equal(before, cache.GetQueueTotals(1));
        Assert.Equal(counters.OrderBy(pair => pair.Key), cache.GetPersistentCounters().OrderBy(pair => pair.Key));
        var result = await cache.MigrateAsync(command);
        Assert.Equal(preflight.Error, result.Error);
        Assert.Equal(preflight.Quota, result.MigrationQuota);
        Assert.Null(cache.ReadMigrationReceipt(command.OperationId));
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("missing")]
    [InlineData("otherSource")]
    [InlineData("wrongId")]
    [InlineData("wrongGeneration")]
    [InlineData("over1000")]
    public async Task PreparationRefusesTheEntireInvalidSelection(string invalid)
    {
        using var data = TemporaryDirectory.Create();
        await using var runtime = RuntimeTesting.CreateRuntime([], data.Path);
        runtime.StartCore();
        var cache = runtime.Deliveries;
        await cache.EnqueueAsync([Draft()], Token);
        var key = Key(Assert.Single(cache.ReadPending(1, 0, 10)));
        var keys = invalid switch
        {
            "duplicate" => new[] { key, key },
            "missing" => [key, new(key.Sequence + 1, Guid.NewGuid(), null)],
            "wrongId" => [key with { DeliveryId = Guid.NewGuid() }],
            "wrongGeneration" => [key with { MigrationId = 1 }],
            "over1000" => Enumerable.Range(1, 1001).Select(i => new DeliveryMigrationKey(i, Guid.NewGuid(), null)).ToArray(),
            _ => [key],
        };
        if (invalid is "duplicate" or "over1000")
        { Assert.Throws<ArgumentException>(() => cache.ReadMigrationCandidates(1, keys)); }
        else
        { Assert.Equal(DeliveryMigrationErrors.SelectionChanged, Assert.Throws<DeliveryMigrationSelectionException>(() => cache.ReadMigrationCandidates(invalid == "otherSource" ? 2 : 1, keys)).Code); }
        Assert.Single(cache.ReadPending(1, 0, 10));
    }

    [Theory]
    [InlineData("summary")]
    [InlineData("fingerprint")]
    [InlineData("total")]
    public async Task MetadataBudgetIsCheckedInSqlBeforeMaterializingOversizedStrings(string budget)
    {
        using var data = TemporaryDirectory.Create();
        await using var runtime = RuntimeTesting.CreateRuntime([], data.Path);
        runtime.StartCore();
        var cache = runtime.Deliveries;
        await cache.EnqueueAsync(Enumerable.Range(0, budget == "total" ? 129 : 1).Select(_ => Draft()).ToArray(), Token);
        var keys = cache.ReadPending(1, 0, 1000).Select(Key).ToArray();
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = cache.Options.DatabasePath }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = budget == "fingerprint" ? "UPDATE delivery SET fingerprint = printf('%.*c', 1025, 'x')"
            : $"UPDATE delivery SET fingerprint_summary = printf('%.*c', {(budget == "summary" ? 32769 : 32768)}, 'x')";
        command.ExecuteNonQuery();
        Assert.Equal("delivery.migrationIdentityBudget", Assert.Throws<DeliveryMigrationSelectionException>(() => cache.ReadMigrationCandidates(1, keys)).Code);
    }
}
