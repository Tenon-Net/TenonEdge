using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.Runtime.Deliveries;

namespace TenonEdge.IntegrationTests.Runtime;

public sealed class DeliveryResponseSummaryTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static HttpResponseSummary Summary(string text = "对方仅接受整数") => new(422, HttpResponseSummaryState.Captured, text);
    private static DeliveryDraft Draft() => new(Guid.NewGuid(), 1, Guid.NewGuid(), "device", 1,
        "{\"value\":9007199254740993}"u8.ToArray(), "fp", "summary", targetAddress: "events");

    [Fact]
    public async Task FailureSummaryPersistsAtomicallyWithoutLeakingIntoMetadataFiltersOrExport()
    {
        using var data = TemporaryDirectory.Create();
        var draft = Draft();
        await using (var runtime = RuntimeTesting.CreateRuntime([], data.Path))
        {
            runtime.StartCore();
            var cache = runtime.Deliveries;
            var seq = Assert.Single((await cache.EnqueueAsync([draft], Token)).Seqs);
            Assert.True((await cache.RecordFailureAsync(new(seq, 3, "HTTP 422", true, false, Summary()), Token)).Committed);
            var record = Assert.Single(cache.ReadFailed(1, 0, 10));
            Assert.True(record.ByteSize > draft.ByteSize);
            Assert.Equal("HTTP 422", record.LastError);
            var firstBytes = record.ByteSize;
            Assert.True((await cache.RecordFailureAsync(new(seq, 3, "HTTP 422", true, false, Summary("not a new result")), Token)).Committed);
            Assert.Equal(firstBytes, Assert.Single(cache.ReadFailed(1, 0, 10)).ByteSize);
            Assert.Equal(1, cache.GetFailureTotals(1).Rejected);
            Assert.Equal("对方仅接受整数", cache.InspectBody(draft.DeliveryId, DeliveryState.Failed)!.ResponseSummary!.Text);
            Assert.Empty(cache.Inspect(new(DeliveryState.Failed, Reason: "对方")).Items);
            Assert.DoesNotContain("对方", JsonSerializer.Serialize(cache.Inspect(new(DeliveryState.Failed))), StringComparison.Ordinal);
            using var spool = new MemoryStream();
            var review = cache.ReviewExport(new(DeliveryState.Failed), Token);
            Assert.True(await cache.WriteExportSpoolAsync(review, spool, Token));
            var export = Encoding.UTF8.GetString(spool.ToArray());
            Assert.DoesNotContain("responseSummary", export, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("对方", export, StringComparison.Ordinal);
            await runtime.StopAsync(Token);
        }
        await using var reopened = RuntimeTesting.CreateRuntime([], data.Path);
        reopened.StartCore();
        var body = reopened.Deliveries.InspectBody(draft.DeliveryId, DeliveryState.Failed)!;
        Assert.Equal(422, body.ResponseSummary!.StatusCode);
        Assert.Equal(HttpResponseSummaryState.Captured, body.ResponseSummary.State);
        Assert.Equal("对方仅接受整数", body.ResponseSummary.Text);
        Assert.Equal(Encoding.UTF8.GetString(draft.Payload.Span), body.Content);
    }

    [Fact]
    public async Task RetryResultsReplaceRatherThanAccumulateSummaryAndMissingSummaryClearsOldResponse()
    {
        using var data = TemporaryDirectory.Create();
        await using var runtime = RuntimeTesting.CreateRuntime([], data.Path);
        runtime.StartCore();
        var cache = runtime.Deliveries;
        var draft = Draft();
        var seq = Assert.Single((await cache.EnqueueAsync([draft], Token)).Seqs);
        var first = new FailureRequest(seq, 1, "HTTP retry", false, false, Summary(new string('x', 1024)));
        await cache.RecordFailureAsync(first, Token);
        var firstSize = Assert.Single(cache.ReadPending(1, 0, 10)).ByteSize;
        await cache.RecordFailureAsync(new(seq, 1, "HTTP retry", false, false, Summary(new string('x', 1024))), Token);
        Assert.Equal(firstSize, Assert.Single(cache.ReadPending(1, 0, 10)).ByteSize);
        await cache.RecordFailureAsync(new(seq, 2, "HTTP retry", false, false, Summary("short")), Token);
        Assert.True(Assert.Single(cache.ReadPending(1, 0, 10)).ByteSize < firstSize);
        await cache.RecordFailureAsync(new(seq, 3, "connection failed", false, false), Token);
        Assert.Equal(draft.ByteSize, Assert.Single(cache.ReadPending(1, 0, 10)).ByteSize);
        await cache.QuarantineAsync(seq, DeliveryQuarantineReasons.TargetChanged, Token);
        Assert.Null(cache.InspectBody(draft.DeliveryId, DeliveryState.Quarantined)!.ResponseSummary);
        await cache.DeleteAsync([seq], Token);
        Assert.Null(cache.InspectBody(draft.DeliveryId, DeliveryState.Quarantined));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OptionalSummaryNeverExceedsPendingQuotaOrPreventsRealFailureResult(bool global)
    {
        using var data = TemporaryDirectory.Create();
        var draft = Draft();
        await using var runtime = RuntimeTesting.CreateRuntime([], data.Path, configureCache: options => new()
        {
            DatabasePath = options.DatabasePath,
            ConfigStoreId = options.ConfigStoreId,
            CommitInterval = options.CommitInterval,
            MaximumPendingBytesPerTarget = global ? 100000 : draft.ByteSize,
            MaximumQueueBytes = global ? draft.ByteSize : 100000,
        });
        runtime.StartCore();
        var cache = runtime.Deliveries;
        var seq = Assert.Single((await cache.EnqueueAsync([draft], Token)).Seqs);
        Assert.True((await cache.RecordFailureAsync(new(seq, 7, "HTTP retry", false, false, Summary()), Token)).Committed);
        var pending = Assert.Single(cache.ReadPending(1, 0, 10));
        Assert.Equal(7, pending.FailureCount);
        Assert.Equal(draft.ByteSize, pending.ByteSize);
        Assert.Contains("响应摘要未保存", pending.LastError, StringComparison.Ordinal);
        Assert.Equal(draft.ByteSize, cache.GetQueueTotals(1).PendingBytes);
        // Terminal failures do not consume pending bytes: the same diagnostic may now be kept.
        Assert.True((await cache.RecordFailureAsync(new(seq, 8, "HTTP 422", true, false, Summary()), Token)).Committed);
        Assert.Equal("对方仅接受整数", cache.InspectBody(draft.DeliveryId, DeliveryState.Failed)!.ResponseSummary!.Text);
        Assert.Equal(0, cache.GetQueueTotals(1).PendingBytes);
        Assert.Equal(0, cache.GetQueueTotals(1).QuotaRejected);
    }

    [Fact]
    public async Task ClearingSummaryBeforeManualRetryAllowsExactQuotaAndPreservesFrozenBodyAndIdentity()
    {
        using var data = TemporaryDirectory.Create();
        var draft = Draft();
        await using var runtime = RuntimeTesting.CreateRuntime([], data.Path, configureCache: options => new()
        {
            DatabasePath = options.DatabasePath,
            ConfigStoreId = options.ConfigStoreId,
            CommitInterval = options.CommitInterval,
            MaximumPendingBytesPerTarget = draft.ByteSize,
            MaximumQueueBytes = draft.ByteSize,
        });
        runtime.StartCore();
        var cache = runtime.Deliveries;
        var seq = Assert.Single((await cache.EnqueueAsync([draft], Token)).Seqs);
        await cache.RecordFailureAsync(new(seq, 1, "HTTP 422", true, false, Summary()), Token);
        Assert.True(Assert.Single(cache.ReadFailed(1, 0, 10)).ByteSize > draft.ByteSize);
        Assert.True((await cache.RetryFailedAsync([new(seq, draft.DeliveryId)], new Dictionary<long, string> { [1] = "fp" }, Token)).Committed);
        var pending = Assert.Single(cache.ReadPending(1, 0, 10));
        Assert.True(pending.Seq > seq);
        Assert.Equal(draft.DeliveryId, pending.DeliveryId);
        Assert.Equal(draft.SourceMessageId, pending.SourceMessageId);
        Assert.Equal(draft.Payload.ToArray(), pending.Payload.ToArray());
        Assert.Equal(draft.ByteSize, pending.ByteSize);
        await cache.QuarantineAsync(pending.Seq, DeliveryQuarantineReasons.TargetChanged, Token);
        Assert.Null(cache.InspectBody(draft.DeliveryId, DeliveryState.Quarantined)!.ResponseSummary);
    }

    [Fact]
    public async Task FailureCounterFaultRollsBackSummaryBytesStateAndCountTogether()
    {
        using var data = TemporaryDirectory.Create();
        await using var runtime = RuntimeTesting.CreateRuntime([], data.Path);
        runtime.StartCore();
        var cache = runtime.Deliveries;
        var draft = Draft();
        var seq = Assert.Single((await cache.EnqueueAsync([draft], Token)).Seqs);
        Sql(data.Path, "CREATE TRIGGER fail_totals BEFORE INSERT ON delivery_failure_totals BEGIN SELECT RAISE(ABORT, 'fixture'); END;");
        Assert.Equal(DeliveryWriteStatus.StorageFailed, (await cache.RecordFailureAsync(new(seq, 1, "HTTP 422", true, false, Summary()), Token)).Status);
        var pending = Assert.Single(cache.ReadPending(1, 0, 10));
        Assert.Equal(draft.ByteSize, pending.ByteSize);
        Assert.Equal(0, pending.FailureCount);
        Assert.Equal(0, cache.GetFailureTotals(1).Rejected);
        Sql(data.Path, "DROP TRIGGER fail_totals;");
        await cache.QuarantineAsync(seq, DeliveryQuarantineReasons.TargetChanged, Token);
        Assert.Null(cache.InspectBody(draft.DeliveryId, DeliveryState.Quarantined)!.ResponseSummary);
    }

    [Fact]
    public async Task FailedAreaOverflowDoesNotRetainAnotherSummaryOrReplaceExistingFailure()
    {
        using var data = TemporaryDirectory.Create();
        await using var runtime = RuntimeTesting.CreateRuntime([], data.Path, configureCache: options => new()
        { DatabasePath = options.DatabasePath, ConfigStoreId = options.ConfigStoreId, CommitInterval = options.CommitInterval, MaximumFailedPerTarget = 1 });
        runtime.StartCore();
        var cache = runtime.Deliveries;
        var drafts = new[] { Draft(), Draft() };
        var seqs = (await cache.EnqueueAsync(drafts, Token)).Seqs;
        foreach (var seq in seqs)
        { Assert.True((await cache.RecordFailureAsync(new(seq, 1, "HTTP 422", true, false, Summary()), Token)).Committed); }
        Assert.NotNull(cache.InspectBody(drafts[0].DeliveryId, DeliveryState.Failed)!.ResponseSummary);
        Assert.Null(cache.InspectBody(drafts[1].DeliveryId, DeliveryState.Failed));
        Assert.Equal(2, cache.GetFailureTotals(1).Rejected);
        Assert.Equal(1, cache.GetFailureTotals(1).Overflowed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BothMigrationFormsKeepPreviousResponseAndItsBytesWithoutReplayingFailedRecord(bool sameConnection)
    {
        using var data = TemporaryDirectory.Create();
        await using var runtime = RuntimeTesting.CreateRuntime([], data.Path);
        runtime.StartCore();
        var cache = runtime.Deliveries;
        var draft = Draft();
        var seq = Assert.Single((await cache.EnqueueAsync([draft], Token)).Seqs);
        await cache.RecordFailureAsync(new(seq, 1, "HTTP 422", true, false, Summary()), Token);
        var before = Assert.Single(cache.ReadFailed(1, 0, 10));
        var operation = sameConnection ? DeliveryMigrationCommand.OldTarget(RuntimeTesting.ConfigStoreId, Guid.NewGuid(), 1, "fp", "new-fp", "summary") :
            DeliveryMigrationCommand.Selected(RuntimeTesting.ConfigStoreId, Guid.NewGuid(), 1, 2, "new-fp", "summary", [new(seq, draft.DeliveryId, "fp", null)]);
        Assert.True((await cache.MigrateAsync(operation)).Committed);
        var after = Assert.Single(cache.ReadFailed(sameConnection ? 1 : 2, 0, 10));
        Assert.Equal(before.ByteSize + 4, after.ByteSize);
        Assert.Equal(before.FailedAt, after.FailedAt);
        Assert.Equal(draft.DeliveryId, after.DeliveryId);
        Assert.Equal(draft.Payload.ToArray(), after.Payload.ToArray());
        Assert.Equal("对方仅接受整数", cache.InspectBody(draft.DeliveryId, DeliveryState.Failed)!.ResponseSummary!.Text);
        Assert.Empty(cache.ReadPending(sameConnection ? 1 : 2, 0, 10));
    }

    [Fact]
    public async Task VersionEightUpgradePreservesRecordsReceiptsAndIdentityWithoutInventingResponse()
    {
        using var data = TemporaryDirectory.Create();
        var draft = Draft();
        DeliveryMigrationReceipt receipt;
        await using (var runtime = RuntimeTesting.CreateRuntime([], data.Path))
        {
            runtime.StartCore();
            var cache = runtime.Deliveries;
            var seq = Assert.Single((await cache.EnqueueAsync([draft], Token)).Seqs);
            await cache.RecordFailureAsync(new(seq, 1, "HTTP 422", true, false), Token);
            var result = await cache.MigrateAsync(DeliveryMigrationCommand.OldTarget(RuntimeTesting.ConfigStoreId, Guid.NewGuid(), 1, "fp", "np", "summary"));
            receipt = Assert.IsType<DeliveryMigrationReceipt>(result.Migration);
            await runtime.StopAsync(Token);
        }
        Sql(data.Path, "DROP INDEX ix_delivery_state_connection_bytes; ALTER TABLE delivery DROP COLUMN response_summary; PRAGMA user_version=8;");
        await using var reopened = RuntimeTesting.CreateRuntime([], data.Path);
        reopened.StartCore();
        var record = Assert.Single(reopened.Deliveries.ReadFailed(1, 0, 10));
        Assert.Equal(draft.DeliveryId, record.DeliveryId);
        Assert.Equal(draft.ByteSize, record.ByteSize);
        Assert.Equal(receipt, reopened.Deliveries.ReadMigrationReceipt(receipt.OperationId));
        Assert.Null(reopened.Deliveries.InspectBody(draft.DeliveryId, DeliveryState.Failed)!.ResponseSummary);
        Assert.Equal(10, reopened.Deliveries.GetStatus().Store!.SchemaVersion);
        Assert.Equal(RuntimeTesting.ConfigStoreId, reopened.Deliveries.GetStatus().Store!.ConfigStoreId);
    }

    private static void Sql(string directory, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(directory, "queue.db")};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
