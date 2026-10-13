using System.Net;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Adapters.Http;
using TenonEdge.Adapters.Mqtt;
using TenonEdge.Host.Bootstrap;
using TenonEdge.Host.Features.Audit;
using TenonEdge.Host.Features.Deliveries;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Security;
using TenonEdge.Host.Storage;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.Runtime.Deliveries;

namespace TenonEdge.IntegrationTests.Processes;

public sealed class DeliveryMigrationInterruptionProcessTests(ITestOutputHelper output)
{
    private const string Root = "/api/edge-admin/v1/deliveries/migrations";
    private const int RecordCount = 4;
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(45);
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public static TheoryData<string, bool, string, bool> Boundaries
    {
        get
        {
            var cases = new TheoryData<string, bool, string, bool>();
            foreach (var protocol in new[] { "http", "mqtt" })
            {
                foreach (var intent in new[] { false, true })
                {
                    foreach (var window in new[] { "queue-update", "queue-commit", "result-commit" })
                    {
                        foreach (var interrupt in new[] { false, true })
                        { cases.Add(protocol, intent, window, interrupt); }
                    }
                }
            }

            return cases;
        }
    }

    [Theory]
    [Trait("Category", "Process")]
    [MemberData(nameof(Boundaries))]
    public async Task MigrationCommitBoundariesAreAtomicAndRecoveryAuditsActualOutcome(string protocol, bool intent, string window, bool interrupt)
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "真实 SIGKILL 和原生 SQLite 探针在 Linux 验证。");
        using var work = TemporaryDirectory.Create();
        using var data = TemporaryDirectory.Create();
        var seed = await SeedAsync(data.Path, protocol, intent);
        var oldRows = Rows(data.Path);
        var frozen = Frozen(data.Path);
        var controls = Read(data.Path, "config.db", "SELECT * FROM output_control ORDER BY connection_id");
        Assert.Equal(RecordCount, oldRows.Length);
        File.WriteAllText(Path.Combine(work.Path, DeliveryMigrationTransactionGate.ArmedFile), window);
        Guid operation;
        var queueCommitted = window == DeliveryMigrationTransactionGate.ResultCommit;
        await using (var host = HostProcess.StartMigrationProbe(work.Path, data.Path))
        {
            Task<HttpResponseMessage>? confirmation = null;
            try
            {
                using var api = await ClientAsync(host);
                if (!intent)
                {
                    using var prepared = await api.PostJsonAsync(Root + "/prepare", new DeliveryMigrationPrepareRequest(1, 2, seed.Items), Token);
                    Assert.True(prepared.IsSuccessStatusCode, await prepared.Content.ReadAsStringAsync(Token));
                    var review = await prepared.ReadJsonAsync(Token);
                    Assert.True(review.GetProperty("canConfirm").GetBoolean());
                    operation = review.GetProperty("id").GetGuid();
                    confirmation = api.PostJsonAsync($"{Root}/{operation}/confirm", new { sameOwnerConfirmed = true }, Token);
                }
                else
                {
                    await UntilAsync(() => Attempts(data.Path).Length == 1);
                    operation = Guid.Parse(Attempts(data.Path)[0].Id);
                }
                var checkpoint = await GateAsync(work.Path);
                Assert.Equal(window, checkpoint.GetProperty("window").GetString());
                Assert.Equal(Path.Combine(data.Path, queueCommitted ? "config.db" : "queue.db"), checkpoint.GetProperty("database").GetString());
                if (!queueCommitted)
                {
                    Assert.Equal(1, checkpoint.GetProperty("receiptNotifications").GetInt32());
                    Assert.Equal(window == DeliveryMigrationTransactionGate.DuringUpdate ? 2 : RecordCount,
                        checkpoint.GetProperty("deliveryUpdateNotifications").GetInt32());
                    Assert.Equal(oldRows, Rows(data.Path));
                    AssertQueueResult(data.Path, seed, migrated: false);
                }
                else
                {
                    Assert.Equal(1, checkpoint.GetProperty("resultUpdateNotifications").GetInt32());
                    Assert.Equal(1, checkpoint.GetProperty("auditInsertNotifications").GetInt32());
                    AssertQueueResult(data.Path, seed, migrated: true);
                }
                // 独立只读连接：config 的结果和审计尚不可见，即使原事务已 UPDATE/INSERT。
                AssertAttempt(data.Path, operation, "started", null);
                AssertAudit(data.Path, operation, finished: false, committed: false);
                if (intent)
                { AssertIntent(data.Path, seed, "waiting", operation); }
                Assert.Equal(frozen, Frozen(data.Path));
                Assert.Equal(controls, Read(data.Path, "config.db", "SELECT * FROM output_control ORDER BY connection_id"));
                output.WriteLine($"CHECKPOINT protocol={protocol}, intent={intent}, window={window}: independent readers observe queueCommitted={queueCommitted}, started only, no result audit; frozen data and pause unchanged. {checkpoint}");
                if (interrupt)
                {
                    await host.KillAsync(Token);
                    Assert.Equal(137, await host.WaitForExitAsync(Deadline, Token));
                    Assert.DoesNotContain("TEST MIGRATION GATE RELEASED", host.Output, StringComparison.Ordinal);
                    if (confirmation is not null)
                    { await Assert.ThrowsAsync<HttpRequestException>(async () => { using var response = await confirmation; }); }
                    // 原宿主尚未重启：先核对持久状态，不能把形式 A 新尝试的成功当作原尝试已提交。
                    AssertQueueResult(data.Path, seed, queueCommitted);
                    if (!queueCommitted)
                    { Assert.Equal(oldRows, Rows(data.Path)); }
                    AssertAttempt(data.Path, operation, "started", null);
                    AssertAudit(data.Path, operation, finished: false, committed: false);
                }
                else
                {
                    File.WriteAllText(Path.Combine(work.Path, DeliveryMigrationTransactionGate.ReleasedFile), "normal release");
                    if (confirmation is not null)
                    {
                        using var response = await confirmation;
                        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Token));
                        Assert.Equal(RecordCount, (await response.ReadJsonAsync(Token)).GetProperty("migratedRecords").GetInt32());
                    }
                    await UntilAsync(() => Attempts(data.Path).Single().Status == "completed");
                    AssertAttempt(data.Path, operation, "completed", RecordCount);
                    AssertAudit(data.Path, operation, finished: true, committed: true);
                    AssertQueueResult(data.Path, seed, migrated: true);
                    if (intent)
                    { AssertIntent(data.Path, seed, "completed", operation); }
                    await StopAsync(host);
                    Assert.Contains("TEST MIGRATION GATE RELEASED", host.Output, StringComparison.Ordinal);
                }
            }
            finally
            {
                File.WriteAllText(Path.Combine(work.Path, DeliveryMigrationTransactionGate.ReleasedFile), "cleanup release");
                // 在失败路径也收取 POST 的结果，避免遗留后台请求；Dispose 会终止未退出的测试子进程。
                if (confirmation is { IsCompleted: false })
                {
                    try
                    { using var response = await confirmation.WaitAsync(Deadline, Token); }
                    catch (Exception error) when (error is HttpRequestException or OperationCanceledException or TimeoutException)
                    { output.WriteLine($"Confirmation cleanup: {error.GetType().Name}"); }
                }
                output.WriteLine(host.Output);
            }
        }

        var originalCommitted = !interrupt || queueCommitted;
        // 两次均使用未注入探针的产品 Host；第一次补记/续跑，第二次证明结果和审计幂等。
        string? history = null;
        for (var restart = 0; restart < 2; restart++)
        {
            await using var host = HostProcess.Start(work.Path, "Production", data.Path);
            try
            {
                using var api = await ClientAsync(host);
                var resumed = intent && !originalCommitted;
                if (resumed)
                { await UntilAsync(() => Attempts(data.Path).Any(attempt => attempt.Id != operation.ToString("D") && attempt.Status == "completed")); }
                AssertAttempt(data.Path, operation, originalCommitted ? "completed" : "interrupted", originalCommitted ? RecordCount : 0);
                AssertAudit(data.Path, operation, finished: true, committed: originalCommitted);
                var attempts = Attempts(data.Path);
                Assert.Equal(resumed ? 2 : 1, attempts.Length);
                if (resumed)
                {
                    var next = Guid.Parse(Assert.Single(attempts, attempt => attempt.Id != operation.ToString("D")).Id);
                    AssertAttempt(data.Path, next, "completed", RecordCount);
                    AssertAudit(data.Path, next, finished: true, committed: true);
                    AssertIntent(data.Path, seed, "completed", next);
                }
                else if (intent)
                { AssertIntent(data.Path, seed, "completed", operation); }
                AssertQueueResult(data.Path, seed, originalCommitted || resumed);
                Assert.Equal(frozen, Frozen(data.Path));
                Assert.Equal(controls, Read(data.Path, "config.db", "SELECT * FROM output_control ORDER BY connection_id"));
                var outcome = await api.GetJsonAsync($"{Root}/{operation}", Token);
                Assert.Equal(originalCommitted ? "completed" : "interrupted", outcome.GetProperty("status").GetString());
                Assert.Equal(originalCommitted ? RecordCount : 0, outcome.GetProperty("affectedRecords").GetInt32());
                var current = Read(data.Path, "config.db", "SELECT * FROM delivery_migration ORDER BY started_at, operation_id") +
                    Read(data.Path, "config.db", "SELECT * FROM audit_log WHERE action LIKE 'delivery.migration%' ORDER BY id") +
                    Read(data.Path, "config.db", "SELECT * FROM delivery_migration_intent ORDER BY intent_id");
                if (history is not null)
                { Assert.Equal(history, current); }
                history = current;
                await StopAsync(host);
                output.WriteLine($"RECOVERED restart={restart + 1}, protocol={protocol}, intent={intent}, window={window}, SIGKILL={interrupt}: originalCommitted={originalCommitted}, resumedWithNewId={resumed}, attempts={attempts.Length}; frozen FIFO/pause preserved; SIGTERM0.");
            }
            finally { output.WriteLine(host.Output); }
        }
    }

    private static async Task<Seed> SeedAsync(string data, string protocol, bool intent)
    {
        // 仅准备独立持久测试资料：不是协议采集证据。中断发生在后续独立进程的原迁移/审计事务中。
        await using var factory = new FixedDataDirectoryApplicationFactory(data);
        var services = factory.Services;
        var cache = services.GetRequiredService<DeliveryCache>();
        var versions = services.GetRequiredService<RuntimeVersionStore>();
        var source = Identity(protocol, "old.test", 1);
        var target = Identity(protocol, "new.test", intent ? 1 : 2);
        versions.Commit(versions.Freeze(Plan(protocol, intent ? [(1, "old.test")] : [(1, "old.test"), (2, "new.test")]),
            RuntimeSnapshotOrigins.Seed, null, "system"), null);
        var records = Enumerable.Range(0, RecordCount).Select(index =>
        {
            var id = Guid.NewGuid();
            return new DeliveryDraft(id, 1, Guid.NewGuid(), "meter", 1, JsonSerializer.SerializeToUtf8Bytes(new { value = index }),
                source.Fingerprint, source.Summary, protocol == "http" ? "send" : "original/topic/" + index,
                qualityOfService: protocol == "mqtt" ? index % 2 : null,
                headers: protocol == "http" ? new Dictionary<string, string> { ["Idempotency-Key"] = id.ToString("D") } : null,
                protocolMetadata: protocol == "http" ? HttpFrozenRequest.Freeze(new HttpOutputSettings { Path = "send" }) : null);
        }).ToArray();
        Assert.True((await cache.EnqueueAsync(records, Token)).Committed);
        var rows = cache.ReadPending(1, 0, 100);
        Assert.Equal(RecordCount, rows.Count);
        Assert.True((await cache.RecordFailureAsync(new(rows[0].Seq, 3, "test rejection", true, false), Token)).Committed);
        Assert.True((await cache.QuarantineAsync(rows[1].Seq, DeliveryQuarantineReasons.TargetChanged, Token)).Committed);
        DeliveryMigrationIntentDraft? draft = null;
        if (intent)
        {
            draft = new(new(cache.Options.ConfigStoreId, Guid.NewGuid(), 1, source.Fingerprint), source.Recipient,
                target.Recipient.Address, true, target);
            versions.Commit(versions.FreezeDraft(Plan(protocol, [(1, "new.test")]), "0", "admin"),
                new(AuditActions.SnapshotApplied, AuditResults.Success, "admin", "192.0.2.10"), markRunning: true, migrationIntents: [draft]);
            versions.ClearRunningMarker(2); // 夹具未激活过 r2；这不是中断恢复证据。
            Assert.True((await services.GetRequiredService<RuntimeCoordinator>().Runtime.HoldMigrationAsync(draft.Hold, null, Token)).Committed);
        }
        var controls = services.GetRequiredService<OutputControlStore>();
        controls.Write(1, new(true, true), "admin", null);
        if (!intent)
        { controls.Write(2, new(true, true), "admin", null); }
        TestAccounts.Create(services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        return new(intent ? 1 : 2, target, draft?.Hold.IntentId,
            rows.Select(row => new DeliveryMigrationKey(row.Seq, row.DeliveryId, row.MigrationId)).ToArray());
    }

    private static OutputTargetIdentity Identity(string protocol, string host, long id) => protocol == "http"
        ? new(new HttpOutputSettings { ConnectionId = id, BaseAddress = $"http://{host}/base/", QueueOnly = true }.Fingerprint(null),
            OutputRecipientIdentity.Http($"http://{host}/base/", null))
        : new(new MqttOutputSettings { ConnectionId = id, Host = host, QueueOnly = true }.Fingerprint(null, null),
            OutputRecipientIdentity.Mqtt($"mqtt://{host}:1883", false, null));

    private static RuntimePlanDocument Plan(string protocol, (long Id, string Host)[] connections) =>
        RuntimePlanDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            units = connections.Select(item => new
            {
                kind = "output." + protocol,
                key = "queue-" + item.Id,
                configuration = protocol == "http"
                    ? (object)new { connectionId = item.Id, baseAddress = $"http://{item.Host}/base/", path = "", queueOnly = true, inputs = Array.Empty<string>() }
                    : new { connectionId = item.Id, host = item.Host, queueOnly = true, inputs = Array.Empty<string>() }
            }),
        }));

    private static void AssertQueueResult(string data, Seed seed, bool migrated)
    {
        var rows = Rows(data);
        Assert.Equal(RecordCount, rows.Length);
        Assert.Equal(migrated ? 1 : 0, Scalar(data, "queue.db", "SELECT count(*) FROM delivery_migration_receipt"));
        Assert.Equal(migrated ? RecordCount : 0, Scalar(data, "queue.db", "SELECT COALESCE((SELECT value FROM queue_counters WHERE name='manuallyMigratedRecords'), 0)"));
        if (!migrated)
        { return; }
        Assert.Equal(RecordCount, Scalar(data, "queue.db", "SELECT affected_records FROM delivery_migration_receipt"));
        Assert.Equal(1, Scalar(data, "queue.db", "SELECT count(DISTINCT migration_id) FROM delivery"));
        var receipt = Scalar(data, "queue.db", "SELECT migration_id FROM delivery_migration_receipt");
        Assert.Equal(1, Scalar(data, "queue.db", "SELECT source_connection_id FROM delivery_migration_receipt"));
        Assert.Equal(seed.Target, Scalar(data, "queue.db", "SELECT target_connection_id FROM delivery_migration_receipt"));
        for (var index = 0; index < rows.Length; index++)
        {
            Assert.Equal(seed.Target, rows[index].Connection);
            Assert.Equal(seed.Identity.Fingerprint, rows[index].Fingerprint);
            Assert.Equal(seed.Identity.Summary, rows[index].Summary);
            Assert.Equal(index == 0 ? "failed" : "pending", rows[index].State);
            Assert.Equal(receipt, rows[index].Migration);
            Assert.Null(rows[index].Intent);
            Assert.Null(rows[index].PreviousState);
            Assert.Null(rows[index].Quarantine);
            Assert.Equal(0, rows[index].Failures);
        }
    }

    private static void AssertAttempt(string data, Guid operation, string status, int? count)
    {
        var attempt = Assert.Single(Attempts(data), item => item.Id == operation.ToString("D"));
        Assert.Equal(status, attempt.Status);
        Assert.Equal(count, attempt.Count);
        if (status == "completed")
        {
            Assert.Equal(JsonSerializer.Serialize(new[] { new[] { operation.ToString("D") } }),
            Read(data, "queue.db", "SELECT operation_id FROM delivery_migration_receipt"));
        }
        Assert.Equal(status == "interrupted" ? "delivery.migrationInterrupted" : null, attempt.Error);
    }

    private static void AssertAudit(string data, Guid operation, bool finished, bool committed)
    {
        using var connection = Open(data, "config.db");
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT action, result, account_name, detail FROM audit_log WHERE target=$id AND action LIKE 'delivery.migration%' ORDER BY id";
        command.Parameters.AddWithValue("$id", operation.ToString("D"));
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("delivery.migrationStarted", reader.GetString(0));
        Assert.Equal("success", reader.GetString(1));
        Assert.Equal("admin", reader.GetString(2));
        Assert.Equal(finished, reader.Read());
        if (finished)
        {
            Assert.Equal("delivery.migrationFinished", reader.GetString(0));
            Assert.Equal(committed ? "success" : "failure", reader.GetString(1));
            Assert.Equal("admin", reader.GetString(2));
            using var detail = JsonDocument.Parse(reader.GetString(3));
            Assert.Equal(committed ? RecordCount : 0, detail.RootElement.GetProperty("AffectedRecords").GetInt32());
            if (!committed)
            { Assert.Equal("未执行：进程中断。", detail.RootElement.GetProperty("message").GetString()); }
        }
        Assert.False(reader.Read());
    }

    private static void AssertIntent(string data, Seed seed, string status, Guid operation)
    {
        using var connection = Open(data, "config.db");
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT status, last_operation_id, intent_id FROM delivery_migration_intent";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(status, reader.GetString(0));
        Assert.Equal(operation.ToString("D"), reader.GetString(1));
        Assert.Equal(seed.Intent!.Value.ToString("D"), reader.GetString(2));
        Assert.False(reader.Read());
    }

    private static async Task<HttpClient> ClientAsync(HostProcess host)
    {
        var client = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() })
        { BaseAddress = await host.WaitForListeningAddressAsync(Deadline, Token), Timeout = Deadline };
        try
        {
            await host.WaitUntilReadyAsync(client, Deadline, Token);
            using var login = await client.LoginAsync("admin", ApiRequests.StrongPassword, Token);
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            await client.UseSessionAsync(login, Token);
            return client;
        }
        catch { client.Dispose(); throw; }
    }

    private static async Task StopAsync(HostProcess host)
    {
        await host.RequestStopAsync(Token);
        Assert.Equal(HostExitCodes.Success, await host.WaitForExitAsync(Deadline, Token));
    }
    private static async Task UntilAsync(Func<bool> condition)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
        deadline.CancelAfter(Deadline);
        while (!condition())
        { await Task.Delay(25, deadline.Token); }
    }
    private static async Task<JsonElement> GateAsync(string work)
    {
        var path = Path.Combine(work, DeliveryMigrationTransactionGate.EnteredFile);
        await UntilAsync(() => File.Exists(path));
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(path, Token));
        return json.RootElement.Clone();
    }
    private static SqliteConnection Open(string data, string file)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = Path.Combine(data, file), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }
    private static long Scalar(string data, string file, string sql)
    {
        using var connection = Open(data, file);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }
    private static string Read(string data, string file, string sql)
    {
        using var connection = Open(data, file);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var rows = new List<object[]>();
        while (reader.Read())
        {
            var values = new object[reader.FieldCount];
            reader.GetValues(values);
            rows.Add(values);
        }
        return JsonSerializer.Serialize(rows);
    }
    private static string Frozen(string data) => Read(data, "queue.db", """
        SELECT seq, delivery_id, source_message_id, device_key, route_id, node_label, revision, payload,
            target_address, qos, headers, created_at, failed_at, protocol_metadata, last_error, clock_anomaly FROM delivery ORDER BY seq
        """);
    private static Row[] Rows(string data)
    {
        using var connection = Open(data, "queue.db");
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT connection_id, fingerprint, fingerprint_summary, state, migration_id, migration_intent_id, migration_previous_state, quarantine_reason, failure_count FROM delivery ORDER BY seq";
        using var reader = command.ExecuteReader();
        var rows = new List<Row>();
        while (reader.Read())
        {
            rows.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetInt64(4),
                reader.IsDBNull(5) ? null : reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7), reader.GetInt32(8)));
        }
        return rows.ToArray();
    }
    private static Attempt[] Attempts(string data)
    {
        using var connection = Open(data, "config.db");
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT operation_id, status, affected_records, error_code FROM delivery_migration ORDER BY started_at, operation_id";
        using var reader = command.ExecuteReader();
        var rows = new List<Attempt>();
        while (reader.Read())
        { rows.Add(new(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetInt32(2), reader.IsDBNull(3) ? null : reader.GetString(3))); }
        return rows.ToArray();
    }
    private sealed record Seed(long Target, OutputTargetIdentity Identity, Guid? Intent, DeliveryMigrationKey[] Items);
    private sealed record Row(long Connection, string Fingerprint, string Summary, string State, long? Migration, string? Intent, string? PreviousState, string? Quarantine, int Failures);
    private sealed record Attempt(string Id, string Status, int? Count, string? Error);
}
