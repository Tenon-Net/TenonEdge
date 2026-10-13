using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.Runtime.Deliveries;

namespace TenonEdge.IntegrationTests.Runtime;

public sealed class DeliveryExportTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(DeliveryState.Failed)]
    [InlineData(DeliveryState.Quarantined)]
    public async Task CompleteBodiesRoundTripWithoutParsingNumbersOrExportingPrivateColumns(DeliveryState state)
    {
        using var data = TemporaryDirectory.Create();
        await using var runtime = RuntimeTesting.CreateRuntime([], data.Path);
        runtime.StartCore();
        var cache = runtime.Deliveries;
        var bodies = new[]
        {
            Encoding.UTF8.GetBytes(" {\"n\":9007199254740993123456789,\"v\":1.23000000000000001e+38}\n\t\"设备😀\"\0"),
            Enumerable.Repeat((byte)0, DeliveryDraft.MaximumPayloadBytes).ToArray(),
            new byte[] { 0xff, 0xfe, 0xf0, 0x9f },
            Encoding.UTF8.GetBytes(new string('x', 65535) + "😀tail"),
        };
        var drafts = bodies.Select(body => Draft(body: body)).ToArray();
        var seqs = (await cache.EnqueueAsync(drafts, Token)).Seqs;
        foreach (var seq in seqs)
        { await Move(cache, seq, state); }
        await cache.EnqueueAsync([Draft()], Token); // Pending is never exported.
        var counters = cache.GetPersistentCounters();
        var review = cache.ReviewExport(new(state), Token);
        Assert.Equal(4, review.Count);
        Assert.Equal(bodies.Sum(body => (long)body.Length), review.PayloadBytes);
        Assert.NotNull(review.FirstCreatedAt);
        Assert.NotNull(review.LastCreatedAt);
        await using var spool = new MemoryStream();
        Assert.True(await cache.WriteExportSpoolAsync(review, spool, Token));
        Assert.True(spool.Length <= review.MaximumFileBytes);
        var lines = Encoding.UTF8.GetString(spool.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(4, lines.Length);
        for (var i = 0; i < lines.Length; i++)
        {
            using var json = JsonDocument.Parse(lines[i]);
            var row = json.RootElement;
            Assert.Equal(1, row.GetProperty("formatVersion").GetInt32());
            Assert.Equal(seqs[i].ToString(CultureInfo.InvariantCulture), row.GetProperty("sequence").GetString());
            Assert.Equal(drafts[i].DeliveryId, row.GetProperty("deliveryId").GetGuid());
            Assert.Equal("1", row.GetProperty("connectionId").GetString());
            Assert.Equal("meter", row.GetProperty("deviceKey").GetString());
            Assert.Equal(state == DeliveryState.Failed ? "failed" : "quarantined", row.GetProperty("state").GetString());
            Assert.Equal(bodies[i].Length, row.GetProperty("payloadBytes").GetInt64());
            var content = row.GetProperty("content").GetString()!;
            Assert.Equal(i == 2 ? "base64" : "utf-8", row.GetProperty("encoding").GetString());
            Assert.Equal(bodies[i], i == 2 ? Convert.FromBase64String(content) : Encoding.UTF8.GetBytes(content));
            foreach (var name in new[] { "payload", "headers", "fingerprint", "fingerprintSummary", "targetAddress", "protocolMetadata" })
            { Assert.False(row.TryGetProperty(name, out _)); }
            Assert.DoesNotContain("private-", lines[i], StringComparison.Ordinal);
        }
        Assert.Single(cache.ReadPending(1, 0, 100));
        Assert.Equal(4, cache.Inspect(new(state)).Items.Length);
        Assert.Equal(counters.OrderBy(pair => pair.Key), cache.GetPersistentCounters().OrderBy(pair => pair.Key));
        Assert.Equal(0, cache.GetStatus().DeletedRecords);
    }

    [Fact]
    public async Task MoreThanOneThousandRecordsAndSixtyFourMiBAreNotSilentlyTruncated()
    {
        using var data = TemporaryDirectory.Create();
        await using var runtime = RuntimeTesting.CreateRuntime([], data.Path);
        runtime.StartCore();
        var cache = runtime.Deliveries;
        var body = Enumerable.Repeat((byte)'x', 70 * 1024).ToArray();
        for (var offset = 0; offset < 1025; offset += 32)
        {
            var drafts = Enumerable.Range(offset, Math.Min(32, 1025 - offset)).Select(i => Draft(i % 2 + 1, body: body)).ToArray();
            var result = await cache.EnqueueAsync(drafts, Token);
            Assert.True(result.Committed);
            Assert.Equal(drafts.Length, result.Seqs.Count);
        }
        await cache.ReconcileAsync(new Dictionary<long, string>(), true, Token);
        var review = cache.ReviewExport(new(DeliveryState.Quarantined), Token);
        Assert.Equal(1025, review.Count);
        Assert.True(review.PayloadBytes > 64L * 1024 * 1024);
        var path = Path.Combine(data.Path, "fixture-export.jsonl");
        await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.True(await cache.WriteExportSpoolAsync(review, file, Token));
            Assert.True(file.Length > 64L * 1024 * 1024);
            Assert.True(file.Length <= review.MaximumFileBytes);
        }
        using var reader = File.OpenText(path);
        var count = 0;
        long previous = 0;
        while (await reader.ReadLineAsync(Token) is { } line)
        {
            using var json = JsonDocument.Parse(line);
            var sequence = long.Parse(json.RootElement.GetProperty("sequence").GetString()!, CultureInfo.InvariantCulture);
            Assert.True(sequence > previous);
            previous = sequence;
            Assert.Equal(new string('x', body.Length), json.RootElement.GetProperty("content").GetString());
            count++;
        }
        Assert.Equal(review.Count, count);
        Assert.Equal(1025, cache.GetQueueReport([]).Usage.QuarantinedCount);
    }

    [Fact]
    public async Task ExportUsesIdenticalLiteralDeviceReasonAndMillisecondBoundariesToInspection()
    {
        using var data = TemporaryDirectory.Create();
        await using var runtime = RuntimeTesting.CreateRuntime([], data.Path);
        runtime.StartCore();
        var cache = runtime.Deliveries;
        var seqs = (await cache.EnqueueAsync([Draft(), Draft(2), Draft(device: "other")], Token)).Seqs;
        foreach (var seq in seqs)
        { await Move(cache, seq, DeliveryState.Failed); }
        var created = cache.Inspect(new(DeliveryState.Failed)).Items[0].CreatedAt;
        foreach (var filter in new DeliveryExportFilter[]
        {
            new(DeliveryState.Failed), new(DeliveryState.Failed, ConnectionId: 2), new(DeliveryState.Failed, DeviceKey: "meter"),
            new(DeliveryState.Failed, DeviceKey: "METER"), new(DeliveryState.Failed, Reason: "%_"),
            new(DeliveryState.Failed, Reason: "http"), new(DeliveryState.Failed, Reason: "' OR 1=1 --"),
            new(DeliveryState.Failed, From: created), new(DeliveryState.Failed, From: created.AddTicks(1)),
            new(DeliveryState.Failed, To: created), new(DeliveryState.Failed, To: created.AddTicks(1)),
            new(DeliveryState.Failed, ConnectionId: 1, DeviceKey: "meter", Reason: "%_", From: created, To: created.AddMilliseconds(1)),
            new(DeliveryState.Quarantined),
        })
        {
            var expected = cache.Inspect(filter.Query).Items;
            var review = cache.ReviewExport(filter, Token);
            Assert.Equal(expected.Length, review.Count);
            await using var spool = new MemoryStream();
            Assert.True(await cache.WriteExportSpoolAsync(review, spool, Token));
            var actual = Encoding.UTF8.GetString(spool.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => { using var json = JsonDocument.Parse(line); return json.RootElement.GetProperty("deliveryId").GetGuid(); }).ToArray();
            Assert.Equal(expected.Select(item => item.DeliveryId), actual);
        }
    }

    [Theory]
    [InlineData("cleanup")]
    [InlineData("retry")]
    [InlineData("quarantine")]
    [InlineData("migration")]
    [InlineData("reason")]
    [InlineData("connection")]
    public async Task ChangedConfirmedSelectionNeverSucceedsAsAPartialExport(string change)
    {
        using var data = TemporaryDirectory.Create();
        await using var runtime = RuntimeTesting.CreateRuntime([], data.Path);
        runtime.StartCore();
        var cache = runtime.Deliveries;
        var draft = Draft();
        var seq = Assert.Single((await cache.EnqueueAsync([draft], Token)).Seqs);
        await Move(cache, seq, DeliveryState.Failed);
        var review = cache.ReviewExport(new(DeliveryState.Failed), Token);
        switch (change)
        {
            case "cleanup":
                await cache.CleanupAsync(new(DeliveryState.Failed, [new(seq, draft.DeliveryId, 1, null)]), Token);
                break;
            case "retry":
                await cache.RetryFailedAsync([new(seq, draft.DeliveryId)], new Dictionary<long, string> { [1] = "private-fingerprint" }, Token);
                await Move(cache, Assert.Single(cache.ReadPending(1, 0, 10)).Seq, DeliveryState.Failed);
                break;
            case "quarantine":
                await cache.QuarantineAsync(seq, DeliveryQuarantineReasons.TargetChanged, Token);
                break;
            // SQL fixtures validate metadata generation, not the still-unimplemented migration feature.
            case "migration":
                Sql(cache, "UPDATE delivery SET migration_id = 9223372036854775807");
                break;
            case "reason":
                Sql(cache, "UPDATE delivery SET last_error = 'changed'");
                break;
            case "connection":
                Sql(cache, "UPDATE delivery SET connection_id = 2");
                break;
            default:
                throw new ArgumentException("Unknown fixture change.", nameof(change));
        }
        await using var spool = new MemoryStream();
        Assert.False(await cache.WriteExportSpoolAsync(review, spool, Token));
    }

    [Fact]
    public async Task NewSequencesAreExcludedButEarlierPendingRecordsEnteringTheFilterInvalidateReview()
    {
        using var data = TemporaryDirectory.Create();
        await using var runtime = RuntimeTesting.CreateRuntime([], data.Path);
        runtime.StartCore();
        var cache = runtime.Deliveries;
        var seqs = (await cache.EnqueueAsync([Draft(), Draft()], Token)).Seqs;
        await Move(cache, seqs[0], DeliveryState.Failed);
        var review = cache.ReviewExport(new(DeliveryState.Failed), Token);
        var added = Assert.Single((await cache.EnqueueAsync([Draft()], Token)).Seqs);
        await Move(cache, added, DeliveryState.Failed);
        await using var first = new MemoryStream();
        Assert.True(await cache.WriteExportSpoolAsync(review, first, Token));
        Assert.Single(Encoding.UTF8.GetString(first.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries));
        await Move(cache, seqs[1], DeliveryState.Failed);
        await using var second = new MemoryStream();
        Assert.False(await cache.WriteExportSpoolAsync(review, second, Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SpoolWaitOwnsNoReadTransactionAndChangesToUnreadPagesInvalidateWholeFile(bool removeUnread)
    {
        using var data = TemporaryDirectory.Create();
        await using var runtime = RuntimeTesting.CreateRuntime([], data.Path);
        runtime.StartCore();
        var cache = runtime.Deliveries;
        var drafts = Enumerable.Range(0, 40).Select(_ => Draft()).ToArray();
        var seqs = (await cache.EnqueueAsync(drafts, Token)).Seqs;
        await cache.ReconcileAsync(new Dictionary<long, string>(), true, Token);
        var review = cache.ReviewExport(new(DeliveryState.Quarantined), Token);
        await using var spool = new CallbackSpool(async () =>
        {
            var index = removeUnread ? 39 : 0;
            Assert.True((await cache.CleanupAsync(new(DeliveryState.Quarantined,
                [new(seqs[index], drafts[index].DeliveryId, 1, null)]), Token)).Committed);
            Assert.True((await cache.EnqueueAsync([Draft()], Token)).Committed);
            // A live export reader/transaction would pin WAL frames and prevent TRUNCATE here.
            using var db = cache.Store.OpenWriteConnection();
            using var command = db.CreateCommand();
            command.CommandText = "PRAGMA busy_timeout = 100";
            command.ExecuteNonQuery();
            command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
            using var reader = command.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal(0, reader.GetInt32(0));
            Assert.Equal(0, reader.GetInt32(1));
        });
        Assert.Equal(!removeUnread, await cache.WriteExportSpoolAsync(review, spool, Token));
        Assert.True(spool.CallbackRan);
        Assert.Equal(removeUnread ? 39 : 40, Encoding.UTF8.GetString(spool.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAndDestinationFailureNeverReturnSuccessOrMutateQueue(bool ioFailure)
    {
        using var data = TemporaryDirectory.Create();
        await using var runtime = RuntimeTesting.CreateRuntime([], data.Path);
        runtime.StartCore();
        var cache = runtime.Deliveries;
        var seqs = (await cache.EnqueueAsync([Draft(), Draft()], Token)).Seqs;
        foreach (var seq in seqs)
        { await Move(cache, seq, DeliveryState.Failed); }
        var review = cache.ReviewExport(new(DeliveryState.Failed), Token);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        await using var spool = new CallbackSpool(() =>
        {
            if (ioFailure)
            { throw new IOException("fixture destination full"); }
            cancellation.Cancel();
            return Task.CompletedTask;
        });
        if (ioFailure)
        { await Assert.ThrowsAsync<IOException>(() => cache.WriteExportSpoolAsync(review, spool, cancellation.Token)); }
        else
        { await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.WriteExportSpoolAsync(review, spool, cancellation.Token)); }
        Assert.Equal(2, cache.Inspect(new(DeliveryState.Failed)).Items.Length);
        await using var retry = new MemoryStream();
        Assert.True(await cache.WriteExportSpoolAsync(review, retry, Token));
    }

    [Fact]
    public async Task InvalidRequestsEmptySelectionAndWrongCacheAreExplicit()
    {
        using var data = TemporaryDirectory.Create();
        await using var runtime = RuntimeTesting.CreateRuntime([], data.Path);
        runtime.StartCore();
        var cache = runtime.Deliveries;
        foreach (var filter in new DeliveryExportFilter[]
        {
            new(DeliveryState.Pending), new((DeliveryState)999), new(DeliveryState.Failed, ConnectionId: 0),
            new(DeliveryState.Failed, ConnectionId: 9007199254740992L), new(DeliveryState.Failed, DeviceKey: new string('x', 129)),
            new(DeliveryState.Failed, Reason: new string('x', 1025)),
            new(DeliveryState.Failed, From: DateTimeOffset.UnixEpoch, To: DateTimeOffset.UnixEpoch),
        })
        { Assert.Throws<ArgumentException>(() => cache.ReviewExport(filter, Token)); }
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => cache.ReviewExport(new(DeliveryState.Failed), cancellation.Token));
        var review = cache.ReviewExport(new(DeliveryState.Failed), Token);
        Assert.Equal(0, review.Count);
        Assert.Equal(0, review.MaximumFileBytes);
        Assert.Null(review.FirstCreatedAt);
        Assert.Null(review.LastCreatedAt);
        await using var spool = new MemoryStream();
        Assert.True(await cache.WriteExportSpoolAsync(review, spool, Token));
        Assert.Equal(0, spool.Length);
        var other = new DeliveryCache(cache.Options, TimeProvider.System);
        await Assert.ThrowsAsync<ArgumentException>(() => other.WriteExportSpoolAsync(review, spool, Token));
        spool.WriteByte(1);
        await Assert.ThrowsAsync<ArgumentException>(() => cache.WriteExportSpoolAsync(review, spool, Token));
        await using var readOnly = new MemoryStream([], writable: false);
        await Assert.ThrowsAsync<ArgumentException>(() => cache.WriteExportSpoolAsync(review, readOnly, Token));
    }

    [Fact]
    public async Task UnreadableQueueAndOversizedBodyAreErrorsNotEmptyOrTruncatedExports()
    {
        using var data = TemporaryDirectory.Create();
        await using var runtime = RuntimeTesting.CreateRuntime([], data.Path);
        runtime.StartCore();
        var cache = runtime.Deliveries;
        var seq = Assert.Single((await cache.EnqueueAsync([Draft()], Token)).Seqs);
        await Move(cache, seq, DeliveryState.Failed);
        var review = cache.ReviewExport(new(DeliveryState.Failed), Token);
        await runtime.StopAsync(Token);
        Sql(cache, "UPDATE delivery SET payload = zeroblob(262145)");
        Assert.Throws<DeliveryStoreException>(() => cache.ReviewExport(new(DeliveryState.Failed), Token));
        await using var spool = new MemoryStream();
        await Assert.ThrowsAsync<DeliveryStoreException>(() => cache.WriteExportSpoolAsync(review, spool, Token));
        Assert.Equal(0, spool.Length);
        Sql(cache, "ALTER TABLE delivery RENAME TO fixture_missing");
        Assert.Throws<SqliteException>(() => cache.ReviewExport(new(DeliveryState.Failed), Token));
    }

    [Fact]
    public async Task ExportSqlNeverReadsConnectionIdentityHeadersAddressOrProtocolMetadata()
    {
        using var data = TemporaryDirectory.Create();
        await using var runtime = RuntimeTesting.CreateRuntime([], data.Path);
        runtime.StartCore();
        var cache = runtime.Deliveries;
        var seq = Assert.Single((await cache.EnqueueAsync([Draft()], Token)).Seqs);
        await Move(cache, seq, DeliveryState.Failed);
        await runtime.StopAsync(Token);
        Sql(cache, """
            ALTER TABLE delivery RENAME TO fixture_delivery;
            CREATE VIEW delivery AS SELECT seq, delivery_id, connection_id, device_key, route_id, node_label,
                revision, payload, created_at, failed_at, failure_count, state, quarantine_reason, last_error,
                migration_id, byte_size, clock_anomaly,
                json('invalid-headers') AS headers, json('invalid-address') AS target_address,
                json('invalid-fingerprint') AS fingerprint, json('invalid-summary') AS fingerprint_summary,
                json('invalid-metadata') AS protocol_metadata FROM fixture_delivery;
            """);
        var review = cache.ReviewExport(new(DeliveryState.Failed), Token);
        await using var spool = new MemoryStream();
        Assert.True(await cache.WriteExportSpoolAsync(review, spool, Token));
        foreach (var column in new[] { "headers", "target_address", "fingerprint", "fingerprint_summary", "protocol_metadata" })
        { Assert.Throws<SqliteException>(() => Sql(cache, "SELECT " + column + " FROM delivery")); }
    }

    private static DeliveryDraft Draft(long target = 1, string device = "meter", byte[]? body = null) =>
        new(Guid.NewGuid(), target, Guid.NewGuid(), device, 1, body ?? "body"u8.ToArray(),
            "private-fingerprint", "private-summary", "private-address",
            headers: new Dictionary<string, string> { ["private-header"] = "private-header-value" }, protocolMetadata: "private-protocol");

    private static async Task Move(DeliveryCache cache, long seq, DeliveryState state)
    {
        var result = state == DeliveryState.Failed
            ? await cache.RecordFailureAsync(new(seq, 3, "HTTP 422 10%_bad", true, false), Token)
            : await cache.QuarantineAsync(seq, DeliveryQuarantineReasons.TargetChanged, Token);
        Assert.True(result.Committed);
    }

    private static void Sql(DeliveryCache cache, string sql)
    {
        using var db = cache.Store.OpenWriteConnection();
        using var command = db.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private sealed class CallbackSpool(Func<Task> callback) : MemoryStream
    {
        public bool CallbackRan { get; private set; }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await base.WriteAsync(buffer, cancellationToken);
            if (!CallbackRan)
            { CallbackRan = true; await callback(); }
        }
    }
}
