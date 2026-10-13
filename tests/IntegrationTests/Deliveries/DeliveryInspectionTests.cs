using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Host.Security;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.Runtime.Deliveries;

namespace TenonEdge.IntegrationTests.Deliveries;

public sealed class DeliveryInspectionTests
{
    private const string Root = "/api/edge-admin/v1/deliveries";
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static DeliveryDraft Draft(long connection = 1, string device = "meter", byte[]? body = null) =>
        new(Guid.NewGuid(), connection, Guid.NewGuid(), device, 1, body ?? "private-body"u8.ToArray(),
            "private-fingerprint", "private-summary", "private-address", headers: new Dictionary<string, string> { ["fixture"] = "private-header" },
            routeId: 42, nodeLabel: "Frozen node", protocolMetadata: "private-protocol");

    [Theory]
    [InlineData("Administrator", HttpStatusCode.OK)]
    [InlineData("Configurator", HttpStatusCode.OK)]
    [InlineData("Viewer", HttpStatusCode.Forbidden)]
    public async Task AllRolesReadMetadataButOnlyEditorsCanReadBody(string role, HttpStatusCode bodyStatus)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "operator", ApiRequests.StrongPassword, Enum.Parse<UserRole>(role));
        var cache = factory.Services.GetRequiredService<DeliveryCache>();
        var draft = Draft();
        var write = await cache.EnqueueAsync([draft], Token);
        Assert.True(write.Committed);
        Assert.True((await cache.RecordFailureAsync(new(write.Seqs[0], 3, "HTTP 422", true, false, new(422, HttpResponseSummaryState.Captured, "private-response")), Token)).Committed);
        var bodyPath = $"{Root}/{draft.DeliveryId}/body?state=failed";
        using var anon = factory.CreateClient();
        foreach (var path in new[] { Root + "?state=failed", bodyPath })
        {
            using var denied = await anon.GetAsync(new Uri(path, UriKind.Relative), Token);
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        }
        using var client = await factory.CreateLoggedInClientAsync("operator", Token);
        using var response = await client.GetAsync(new Uri(Root + "?state=failed", UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        var page = await response.ReadJsonAsync(Token);
        var item = Assert.Single(page.GetProperty("items").EnumerateArray());
        Assert.Equal(draft.DeliveryId, item.GetProperty("deliveryId").GetGuid());
        Assert.Equal("HTTP 422", item.GetProperty("lastError").GetString());
        Assert.Equal(3, item.GetProperty("failureCount").GetInt32());
        Assert.Equal(42, item.GetProperty("routeId").GetInt32());
        Assert.Equal("Frozen node", item.GetProperty("nodeLabel").GetString());
        Assert.True(item.GetProperty("failedAt").GetDateTimeOffset() >= item.GetProperty("createdAt").GetDateTimeOffset());
        Assert.DoesNotContain("private-", page.GetRawText(), StringComparison.Ordinal);
        foreach (var name in new[] { "payload", "headers", "fingerprint", "targetAddress", "protocolMetadata", "responseSummary" })
        { Assert.False(item.TryGetProperty(name, out _)); }
        using var body = await client.GetAsync(new Uri(bodyPath, UriKind.Relative), Token);
        Assert.Equal(bodyStatus, body.StatusCode);
        if (bodyStatus == HttpStatusCode.OK)
        {
            Assert.True(body.Headers.CacheControl!.NoStore);
            var preview = await body.ReadJsonAsync(Token);
            Assert.Equal("private-body", preview.GetProperty("content").GetString());
            Assert.Equal("private-response", preview.GetProperty("responseSummary").GetProperty("text").GetString());
            Assert.Equal(422, preview.GetProperty("responseSummary").GetProperty("statusCode").GetInt32());
            Assert.False(preview.GetProperty("truncated").GetBoolean());
            Assert.Equal("utf-8", preview.GetProperty("encoding").GetString());
            Assert.DoesNotContain("private-header", preview.GetRawText(), StringComparison.Ordinal);
        }
        // Listing and preview never change the queue or initiate a send.
        Assert.Equal(1, cache.GetQueueReport([]).Usage.FailedCount);
        Assert.Empty(TestAccounts.Audit(factory.Services, "delivery.retry"));
    }

    [Fact]
    public async Task KeysetPagesAndLiteralFiltersUseEnqueuedTimeAndRetainOrphans()
    {
        using var data = TemporaryDirectory.Create();
        await using var runtime = RuntimeTesting.CreateRuntime([], data.Path);
        runtime.StartCore();
        var cache = runtime.Deliveries;
        var drafts = Enumerable.Range(0, 105).Select(i => Draft(i < 100 ? 1 : 999, i % 2 == 0 ? "meter" : "other")).ToArray();
        var write = await cache.EnqueueAsync(drafts, Token);
        Assert.True(write.Committed);
        // Targets each fit the genuine failed-area quota (100), rather than bypassing it.
        foreach (var seq in write.Seqs)
        { Assert.True((await cache.RecordFailureAsync(new(seq, 2, "HTTP 422 10%_bad", true, false), Token)).Committed); }
        var page = cache.Inspect(new(DeliveryState.Failed));
        Assert.Equal(100, page.Items.Length);
        Assert.Equal(write.Seqs[99], page.NextSequence);
        var last = cache.Inspect(new(DeliveryState.Failed, After: page.NextSequence!.Value));
        Assert.Equal(5, last.Items.Length);
        Assert.Null(last.NextSequence);
        Assert.All(last.Items, item => Assert.Equal(999, item.ConnectionId));
        Assert.Equal(drafts.Select(d => d.DeliveryId), page.Items.Concat(last.Items).Select(item => item.DeliveryId));
        var created = page.Items[0].CreatedAt;
        Assert.Empty(cache.Inspect(new(DeliveryState.Failed, From: created.AddTicks(1), To: created.AddMilliseconds(1))).Items);
        Assert.NotEmpty(cache.Inspect(new(DeliveryState.Failed, To: created.AddTicks(1))).Items);
        var exact = cache.Inspect(new(DeliveryState.Failed, ConnectionId: 1, DeviceKey: "meter", Reason: "%_",
            From: created, To: page.Items[^1].CreatedAt.AddMilliseconds(1)));
        Assert.Equal(50, exact.Items.Length);
        Assert.Empty(cache.Inspect(new(DeliveryState.Failed, Reason: "' OR 1=1 --")).Items);
        Assert.Empty(cache.Inspect(new(DeliveryState.Failed, DeviceKey: "METer")).Items);
        Assert.Empty(cache.Inspect(new(DeliveryState.Failed, To: created)).Items);
        Assert.Empty(cache.Inspect(new(DeliveryState.Failed, From: last.Items[^1].CreatedAt.AddMilliseconds(1))).Items);
        Assert.Empty(cache.Inspect(new(DeliveryState.Failed, Reason: "http")).Items);
        Assert.True((await cache.QuarantineAsync(write.Seqs[0], DeliveryQuarantineReasons.TargetChanged, Token)).Committed);
        Assert.True((await cache.QuarantineAsync(write.Seqs[1], DeliveryQuarantineReasons.TargetMissing, Token)).Committed);
        var quarantined = cache.Inspect(new(DeliveryState.Quarantined, Reason: "targetChanged"));
        Assert.Equal(drafts[0].DeliveryId, Assert.Single(quarantined.Items).DeliveryId);
        Assert.Null(cache.InspectBody(drafts[0].DeliveryId, DeliveryState.Failed));
        Assert.NotNull(cache.InspectBody(drafts[0].DeliveryId, DeliveryState.Quarantined));
        await cache.DeleteAsync([write.Seqs[0]], Token);
        Assert.Null(cache.InspectBody(drafts[0].DeliveryId, DeliveryState.Quarantined));
        Assert.Empty(cache.Inspect(new(DeliveryState.Quarantined, Reason: "targetChanged")).Items);
        Assert.DoesNotContain("private-", JsonSerializer.Serialize(page), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(65535, false)]
    [InlineData(65536, false)]
    [InlineData(65537, true)]
    [InlineData(262144, true)]
    public async Task BodyIsBoundedAtSixtyFourKiB(int length, bool truncated)
    {
        using var data = TemporaryDirectory.Create();
        await using var runtime = RuntimeTesting.CreateRuntime([], data.Path);
        runtime.StartCore();
        var cache = runtime.Deliveries;
        var draft = Draft(body: Enumerable.Repeat((byte)'a', length).ToArray());
        var write = await cache.EnqueueAsync([draft], Token);
        await cache.QuarantineAsync(write.Seqs[0], DeliveryQuarantineReasons.ConfigStoreChanged, Token);
        var preview = Assert.IsType<DeliveryBodyPreview>(cache.InspectBody(draft.DeliveryId, DeliveryState.Quarantined));
        Assert.Equal(length, preview.TotalBytes);
        Assert.Equal(Math.Min(length, 65536), preview.DisplayedBytes);
        Assert.Equal(preview.DisplayedBytes, preview.Content.Length);
        Assert.Equal(truncated, preview.Truncated);
        Assert.Equal("utf-8", preview.Encoding);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task TruncatedUtf8EndsOnlyOnCompleteCodePoints(int partialBytes)
    {
        using var data = TemporaryDirectory.Create();
        await using var runtime = RuntimeTesting.CreateRuntime([], data.Path);
        runtime.StartCore();
        var prefix = new string('x', 65536 - partialBytes);
        var draft = Draft(body: Encoding.UTF8.GetBytes(prefix + "😀tail"));
        var write = await runtime.Deliveries.EnqueueAsync([draft], Token);
        await runtime.Deliveries.QuarantineAsync(write.Seqs[0], DeliveryQuarantineReasons.TargetChanged, Token);
        var body = Assert.IsType<DeliveryBodyPreview>(runtime.Deliveries.InspectBody(draft.DeliveryId, DeliveryState.Quarantined));
        Assert.Equal(prefix, body.Content);
        Assert.Equal(prefix.Length, body.DisplayedBytes);
        Assert.Equal("utf-8", body.Encoding);
        Assert.True(body.Truncated);
    }

    [Theory]
    [InlineData(new byte[] { 0xff, 0xfe })]
    [InlineData(new byte[] { 0xf0, 0x9f })]
    public async Task InvalidOrIncompleteWholeUtf8UsesExplicitBase64(byte[] payload)
    {
        using var data = TemporaryDirectory.Create();
        await using var runtime = RuntimeTesting.CreateRuntime([], data.Path);
        runtime.StartCore();
        var draft = Draft(body: payload);
        var write = await runtime.Deliveries.EnqueueAsync([draft], Token);
        await runtime.Deliveries.QuarantineAsync(write.Seqs[0], DeliveryQuarantineReasons.TargetChanged, Token);
        var body = Assert.IsType<DeliveryBodyPreview>(runtime.Deliveries.InspectBody(draft.DeliveryId, DeliveryState.Quarantined));
        Assert.Equal("base64", body.Encoding);
        Assert.Equal(payload, Convert.FromBase64String(body.Content));
        Assert.False(body.Truncated);
    }

    [Fact]
    public async Task ReadSnapshotIgnoresUncommittedWritesAndDoesNotParsePrivateColumns()
    {
        using var data = TemporaryDirectory.Create();
        await using var runtime = RuntimeTesting.CreateRuntime([], data.Path);
        runtime.StartCore();
        var cache = runtime.Deliveries;
        var draft = Draft();
        var write = await cache.EnqueueAsync([draft], Token);
        await cache.RecordFailureAsync(new(write.Seqs[0], 1, "HTTP 422", true, false), Token);
        using var db = cache.Store.OpenWriteConnection();
        using var command = db.CreateCommand();
        command.CommandText = "UPDATE delivery SET headers = 'not-json', protocol_metadata = 'not-json'";
        command.ExecuteNonQuery(); // If inspection reused full StoredDelivery, private headers would be parsed and fail.
        Assert.Single(cache.Inspect(new(DeliveryState.Failed)).Items);
        using var transaction = db.BeginTransaction();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM delivery";
        command.ExecuteNonQuery();
        Assert.Single(cache.Inspect(new(DeliveryState.Failed)).Items);
        Assert.NotNull(cache.InspectBody(draft.DeliveryId, DeliveryState.Failed));
        transaction.Commit();
        Assert.Empty(cache.Inspect(new(DeliveryState.Failed)).Items);
        Assert.Null(cache.InspectBody(draft.DeliveryId, DeliveryState.Failed));
    }

    [Fact]
    public async Task MetadataSqlNeverEvaluatesBodyHeadersAddressOrIdentityColumns()
    {
        using var data = TemporaryDirectory.Create();
        await using var runtime = RuntimeTesting.CreateRuntime([], data.Path);
        runtime.StartCore();
        var cache = runtime.Deliveries;
        var write = await cache.EnqueueAsync([Draft()], Token);
        await cache.RecordFailureAsync(new(write.Seqs[0], 1, "HTTP 422", true, false), Token);
        await runtime.StopAsync(Token);
        // 测试侧视图：任何读取敏感列的 SQL 都会执行无效 JSON 并失败，而不只是 DTO 隐藏它们。
        using var db = cache.Store.OpenWriteConnection();
        using var command = db.CreateCommand();
        command.CommandText = """
            ALTER TABLE delivery RENAME TO fixture_delivery;
            CREATE VIEW delivery AS SELECT seq, delivery_id, connection_id, source_message_id, device_key, route_id,
                node_label, revision, created_at, failed_at, failure_count, state, quarantine_reason, last_error,
                migration_id, byte_size, clock_anomaly,
                json('invalid-payload') AS payload, json('invalid-headers') AS headers,
                json('invalid-address') AS target_address, qos, json('invalid-fingerprint') AS fingerprint,
                json('invalid-summary') AS fingerprint_summary, json('invalid-metadata') AS protocol_metadata,
                json('invalid-response') AS response_summary
            FROM fixture_delivery;
            """;
        command.ExecuteNonQuery();
        try
        {
            Assert.Single(cache.Inspect(new(DeliveryState.Failed)).Items);
            foreach (var column in new[] { "payload", "headers", "target_address", "fingerprint", "fingerprint_summary", "protocol_metadata", "response_summary" })
            {
                command.CommandText = "SELECT " + column + " FROM delivery";
                Assert.Throws<SqliteException>(() => command.ExecuteScalar());
            }
        }
        finally
        {
            command.CommandText = "DROP VIEW delivery; ALTER TABLE fixture_delivery RENAME TO delivery";
            command.ExecuteNonQuery();
        }
    }

    [Theory]
    [InlineData("not-json-private-response")]
    [InlineData("{\"StatusCode\":422,\"State\":0,\"Text\":null}")]
    [InlineData("{\"StatusCode\":422,\"State\":99,\"Text\":\"private-response\"}")]
    public async Task CorruptSummaryReturnsFixedFailureWithoutExposingItsContent(string corrupt)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "operator", ApiRequests.StrongPassword, UserRole.Configurator);
        var cache = factory.Services.GetRequiredService<DeliveryCache>();
        var draft = Draft();
        var write = await cache.EnqueueAsync([draft], Token);
        await cache.RecordFailureAsync(new(write.Seqs[0], 1, "HTTP 422", true, false), Token);
        using var db = cache.Store.OpenWriteConnection();
        using var command = db.CreateCommand();
        command.CommandText = "UPDATE delivery SET response_summary=$corrupt";
        command.Parameters.AddWithValue("$corrupt", corrupt);
        command.ExecuteNonQuery();
        using var client = await factory.CreateLoggedInClientAsync("operator", Token);
        using var metadata = await client.GetAsync(new Uri(Root + "?state=failed", UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.OK, metadata.StatusCode);
        using var body = await client.GetAsync(new Uri($"{Root}/{draft.DeliveryId}/body?state=failed", UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, body.StatusCode);
        Assert.Equal("delivery.queueUnavailable", await body.ReadProblemCodeAsync(Token));
        Assert.DoesNotContain("private-response", await body.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);
        Assert.True(body.Headers.CacheControl!.NoStore);
    }

    [Fact]
    public async Task ApiRejectsInvalidFiltersMissingRecordsAndUnavailableQueueExplicitly()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "operator", ApiRequests.StrongPassword, UserRole.Administrator);
        using var client = await factory.CreateLoggedInClientAsync("operator", Token);
        foreach (var query in new[] { "", "?state=pending", "?state=Failed", "?state=2", "?state=failed&limit=101", "?state=failed&limit=0",
            "?state=failed&after=-1", "?state=failed&connectionId=0", "?state=failed&connectionId=9007199254740992",
            "?state=failed&deviceKey=" + new string('x', 129), "?state=failed&reason=" + new string('x', 1025),
            "?state=failed&from=2026-10-13T00:00:00Z&to=2026-10-13T00:00:00Z", "?state=failed&from=not-time" })
        {
            using var invalid = await client.GetAsync(new Uri(Root + query, UriKind.Relative), Token);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }
        foreach (var state in new[] { "failed", "quarantined" })
        {
            using var missing = await client.GetAsync(new Uri($"{Root}/{Guid.NewGuid()}/body?state={state}", UriKind.Relative), Token);
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }
        using var badBody = await client.GetAsync(new Uri($"{Root}/{Guid.Empty}/body?state=failed", UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.BadRequest, badBody.StatusCode);
        var cache = factory.Services.GetRequiredService<DeliveryCache>();
        using var db = cache.Store.OpenWriteConnection();
        using var command = db.CreateCommand();
        command.CommandText = "ALTER TABLE delivery RENAME TO fixture_missing";
        command.ExecuteNonQuery();
        try
        {
            foreach (var path in new[] { Root + "?state=failed", $"{Root}/{Guid.NewGuid()}/body?state=failed" })
            {
                using var unreadable = await client.GetAsync(new Uri(path, UriKind.Relative), Token);
                Assert.Equal(HttpStatusCode.ServiceUnavailable, unreadable.StatusCode);
                Assert.Equal("delivery.queueUnavailable", await unreadable.ReadProblemCodeAsync(Token));
                Assert.True(unreadable.Headers.CacheControl!.NoStore);
            }
        }
        finally
        {
            command.CommandText = "ALTER TABLE fixture_missing RENAME TO delivery";
            command.ExecuteNonQuery();
        }
    }
}
