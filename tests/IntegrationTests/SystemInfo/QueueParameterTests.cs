using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Host.Bootstrap;
using TenonEdge.Host.Features.Audit;
using TenonEdge.Host.Features.SystemInfo;
using TenonEdge.Host.Security;
using TenonEdge.Host.Storage;
using TenonEdge.Host.Storage.Migrations;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.Runtime.Deliveries;
using TenonEdge.Runtime.Diagnostics;

namespace TenonEdge.IntegrationTests.SystemInfo;

public sealed class QueueParameterTests
{
    private const string Endpoint = "/api/edge-admin/v1/system/queue-parameters";
    private const string Action = "system.queueParametersChanged";
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly int[] ConcurrentLimits = [5, 6];

    [Theory]
    [InlineData("Administrator", HttpStatusCode.OK)]
    [InlineData("Configurator", HttpStatusCode.Forbidden)]
    [InlineData("Viewer", HttpStatusCode.Forbidden)]
    public async Task AllRolesReadButOnlyAdministratorChanges(string role, HttpStatusCode expected)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "operator", ApiRequests.StrongPassword, Enum.Parse<UserRole>(role));
        using var client = await factory.CreateLoggedInClientAsync("operator", Token);
        using var get = await client.GetAsync(Endpoint, Token);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.True(get.Headers.CacheControl!.NoStore);
        Assert.Equal(QueueParameterValues.Default, (await Read(client)).Values);
        using var put = await Write(client, QueueParameterValues.Default with { MaximumPendingPerTarget = 1 }, null);
        Assert.Equal(expected, put.StatusCode);
        Assert.Equal(expected == HttpStatusCode.OK ? 1 : 0, TestAccounts.Audit(factory.Services, Action).Count);
    }

    [Fact]
    public async Task AnonymousCsrfConfirmationVersionAndCompleteKnownFieldsAreRequired()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        using var anonymous = factory.CreateClient();
        using var get = await anonymous.GetAsync(Endpoint, Token);
        using var put = await Write(anonymous, QueueParameterValues.Default, null);
        Assert.Equal(HttpStatusCode.Unauthorized, get.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, put.StatusCode);
        using var client = await Admin(factory);
        var valid = JsonSerializer.SerializeToNode(new QueueParameterRequest(QueueParameterValues.Default, null, true), JsonSerializerOptions.Web)!.AsObject();
        foreach (var mutation in new[] { "confirmation", "version", "null", "missing", "extra", "nestedExtra" })
        {
            var body = valid.DeepClone().AsObject();
            switch (mutation)
            {
                case "confirmation":
                    body["confirmed"] = false;
                    break;
                case "version":
                    body.Remove("expectedVersion");
                    break;
                case "null":
                    body["values"] = null;
                    break;
                case "missing":
                    body["values"]!.AsObject().Remove("minimumDiskFreePercent");
                    break;
                case "extra":
                    body["unknown"] = true;
                    break;
                case "nestedExtra":
                    body["values"]!["unknown"] = true;
                    break;
            }
            using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            using var response = await client.PutAsync(Endpoint, content, Token);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        client.DefaultRequestHeaders.Remove(ApiRequests.CsrfHeader);
        using var csrf = await Write(client, QueueParameterValues.Default, null);
        Assert.Equal(HttpStatusCode.Forbidden, csrf.StatusCode);
        Assert.Empty(TestAccounts.Audit(factory.Services, Action));
    }

    [Fact]
    public async Task RangeChecksRejectWithoutPublicationAndBoundaryValuesAreAccepted()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        using var client = await Admin(factory);
        var d = QueueParameterValues.Default;
        QueueParameterValues[] invalid =
        [
            d with { MaximumPendingPerTarget = 0 }, d with { MaximumPendingPerTarget = 1_000_001 },
            d with { MaximumPendingBytesPerTarget = 0 }, d with { MaximumPendingBytesPerTarget = QueueParameterValues.MaximumBytes + 1 },
            d with { MaximumQueueBytes = 0 }, d with { MaximumQueueBytes = QueueParameterValues.MaximumBytes + 1 },
            d with { MaximumFailedPerTarget = 0 }, d with { MaximumFailedGlobal = 1_000_001 },
            d with { PendingRetentionHours = 0 }, d with { PendingRetentionHours = 87601 },
            d with { FailedRetentionHours = 0 }, d with { FailedRetentionHours = 87601 },
            d with { MinimumDiskFreeBytes = -1 }, d with { MinimumDiskFreeBytes = QueueParameterValues.MaximumBytes + 1 },
            d with { MinimumDiskFreePercent = -1 }, d with { MinimumDiskFreePercent = 100 },
        ];
        foreach (var value in invalid)
        {
            using var response = await Write(client, value, null);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        Assert.Null((await Read(client)).Version);
        Assert.Empty(TestAccounts.Audit(factory.Services, Action));
        using var low = await Write(client, new(1, 1, 1, 1, 1, 1, 1, 0, 0), null);
        Assert.Equal(HttpStatusCode.OK, low.StatusCode);
        using var high = await Write(client, new(1_000_000, QueueParameterValues.MaximumBytes, QueueParameterValues.MaximumBytes,
            87600, 1_000_000, 1_000_000, 87600, QueueParameterValues.MaximumBytes, 99), (await Read(client)).Version);
        Assert.Equal(HttpStatusCode.OK, high.StatusCode);
    }

    [Fact]
    public async Task ConcurrentVersionsAndAuditFailureCannotPublishUncommittedValues()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        using var client = await Admin(factory);
        var writes = await Task.WhenAll(ConcurrentLimits.Select(count => Write(client, QueueParameterValues.Default with { MaximumPendingPerTarget = count }, null)));
        try
        {
            Assert.Single(writes, r => r.StatusCode == HttpStatusCode.OK);
            Assert.Single(writes, r => r.StatusCode == HttpStatusCode.Conflict);
        }
        finally { foreach (var response in writes) { response.Dispose(); } }
        var current = await Read(client);
        using var noOp = await Write(client, current.Values, current.Version);
        Assert.Equal(HttpStatusCode.OK, noOp.StatusCode);
        Assert.Equal(current, await Read(client));
        var database = factory.Services.GetRequiredService<ConfigDatabase>();
        database.Client.Ado.ExecuteCommand($"CREATE TRIGGER reject_queue_parameters BEFORE INSERT ON audit_log WHEN NEW.action='{Action}' BEGIN SELECT RAISE(ABORT, 'fixture audit failure'); END;");
        using var failed = await Write(client, current.Values with { MaximumPendingPerTarget = 7 }, current.Version);
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.Equal(current, await Read(client));
        Assert.Equal(current.Values.ToPolicy(), factory.Services.GetRequiredService<IDeliveryPolicySource>().Read());
        Assert.Equal(current.Version, database.Client.Queryable<QueueParameterRecord>().InSingle(1).Version);
        Assert.Single(TestAccounts.Audit(factory.Services, Action));
    }

    [Fact]
    public async Task RestartRetainsParametersAndDoesNotChangeIdentityDraftOrRuntimeVersion()
    {
        using var data = TemporaryDirectory.Create();
        QueueParameterState changed;
        string node;
        await using (var factory = new FixedDataDirectoryApplicationFactory(data.Path))
        {
            using var client = await Admin(factory);
            node = factory.Services.GetRequiredService<GatewayIdentity>().NodeId;
            using var put = await Write(client, QueueParameterValues.Default with { MaximumPendingPerTarget = 13, PendingRetentionHours = 72 }, null);
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);
            changed = await Read(client);
        }
        await using var restarted = new FixedDataDirectoryApplicationFactory(data.Path);
        using var again = await restarted.CreateLoggedInClientAsync("admin", Token);
        Assert.Equal(changed, await Read(again));
        Assert.Equal(changed.Values.ToPolicy(), restarted.Services.GetRequiredService<DeliveryCache>().CurrentPolicy);
        Assert.Equal(node, restarted.Services.GetRequiredService<GatewayIdentity>().NodeId);
        var state = restarted.Services.GetRequiredService<ConfigDatabase>().Client.Queryable<SystemStateRecord>().InSingle(1);
        Assert.Null(state.EffectiveRevision);
        Assert.Null(state.PendingRevision);
        Assert.Single(TestAccounts.Audit(restarted.Services, Action));
    }

    [Fact]
    public async Task ActiveReadLeaseRejectsParameterWritesImmediatelyAndReleaseAllowsSaving()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        using var client = await Admin(factory);
        var store = factory.Services.GetRequiredService<QueueParameterStore>();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var held = Task.Run(() =>
        {
            using var lease = store.AcquireRead();
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10), Token));
            lease.Dispose(); // Idempotent; no second unlock.
        }, Token);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5), Token));
            using var busy = await Write(client, QueueParameterValues.Default with { PendingRetentionHours = 48 }, null);
            Assert.Equal(HttpStatusCode.Conflict, busy.StatusCode);
            Assert.Equal("system.queueParametersBusy", await busy.ReadProblemCodeAsync(Token));
            Assert.Null((await Read(client)).Version);
        }
        finally { release.Set(); await held; }
        using var saved = await Write(client, QueueParameterValues.Default with { PendingRetentionHours = 48 }, null);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
    }

    [Fact]
    public async Task LiveQuotaReductionKeepsOldRecordsAndNewAdmissionAndDiagnosticUseNewPolicy()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        using var client = await Admin(factory);
        var cache = factory.Services.GetRequiredService<DeliveryCache>();
        var drafts = new[] { Draft(1), Draft(1) };
        Assert.Equal(2, (await cache.EnqueueAsync(drafts, Token)).Seqs.Count);
        using var lower = await Write(client, QueueParameterValues.Default with { MaximumPendingPerTarget = 1 }, null);
        Assert.Equal(HttpStatusCode.OK, lower.StatusCode);
        Assert.Same(cache, factory.Services.GetRequiredService<DeliveryCache>());
        Assert.Equal(drafts.Select(d => d.DeliveryId), cache.ReadPending(1, 0, 10).Select(d => d.DeliveryId));
        var result = await cache.EnqueueAsync([Draft(1), Draft(2)], Token);
        Assert.Equal(1, result.QuotaRejectedRecords);
        Assert.Single(result.Seqs);
        using var report = await client.GetAsync("/api/edge-admin/v1/runtime/outputs/1", Token);
        var body = await report.ReadJsonAsync(Token);
        Assert.Equal(1, body.GetProperty("limits").GetProperty("targetCount").GetInt32());
        using var raise = await Write(client, QueueParameterValues.Default with { MaximumPendingPerTarget = 3 }, (await Read(client)).Version);
        Assert.Equal(HttpStatusCode.OK, raise.StatusCode);
        Assert.Single((await cache.EnqueueAsync([Draft(1)], Token)).Seqs);
        using var bytes = await Write(client, QueueParameterValues.Default with { MaximumPendingBytesPerTarget = 1 }, (await Read(client)).Version);
        Assert.Equal(HttpStatusCode.OK, bytes.StatusCode);
        Assert.Equal(1, (await cache.EnqueueAsync([Draft(3)], Token)).QuotaRejectedRecords);
        using var global = await Write(client, QueueParameterValues.Default with { MaximumQueueBytes = 1 }, (await Read(client)).Version);
        Assert.Equal(HttpStatusCode.OK, global.StatusCode);
        Assert.Equal(1, (await cache.EnqueueAsync([Draft(4)], Token)).QuotaRejectedRecords);
        Assert.Equal(3, cache.ReadPending(1, 0, 10).Count);
    }

    [Fact]
    public async Task FailureOverflowRetryAndMigrationRecheckNewLimitsWithoutEvictingOldRows()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        using var client = await Admin(factory);
        var cache = factory.Services.GetRequiredService<DeliveryCache>();
        var seqs = (await cache.EnqueueAsync([Draft(1), Draft(1), Draft(1), Draft(2)], Token)).Seqs;
        await cache.RecordFailureAsync(new(seqs[0], 0, "fixture", true, false), Token);
        await cache.RecordFailureAsync(new(seqs[1], 0, "fixture", true, false), Token);
        using var change = await Write(client, QueueParameterValues.Default with { MaximumFailedPerTarget = 1, MaximumFailedGlobal = 1, MaximumPendingPerTarget = 1 }, null);
        Assert.Equal(HttpStatusCode.OK, change.StatusCode);
        Assert.Equal(2, cache.ReadFailed(1, 0, 10).Count);
        await cache.RecordFailureAsync(new(seqs[2], 0, "fixture", true, false), Token);
        Assert.Equal(2, cache.ReadFailed(1, 0, 10).Count);
        Assert.Equal(1, cache.GetFailureTotals(1).Overflowed);
        var rows = cache.ReadFailed(1, 0, 10);
        var retry = await cache.RetryFailedAsync(rows.Select(r => new DeliveryRetryItem(r.Seq, r.DeliveryId)).ToArray(), new Dictionary<long, string> { [1] = "fp" }, Token);
        Assert.Equal(DeliveryRetryErrors.QuotaExceeded, retry.Error);
        var storeId = factory.Services.GetRequiredService<GatewayIdentity>().ConfigStoreId;
        var selected = rows.Select(r => new DeliveryMigrationItem(r.Seq, r.DeliveryId, r.Fingerprint, r.MigrationId)).ToArray();
        using var raise = await Write(client, QueueParameterValues.Default, (await Read(client)).Version);
        Assert.Equal(HttpStatusCode.OK, raise.StatusCode);
        var command = DeliveryMigrationCommand.Selected(storeId, Guid.NewGuid(), 1, 2, "next", "next", selected);
        Assert.Null(cache.PreflightMigration(command).Error);
        using var lower = await Write(client, QueueParameterValues.Default with { MaximumPendingPerTarget = 1 }, (await Read(client)).Version);
        Assert.Equal(HttpStatusCode.OK, lower.StatusCode);
        Assert.Equal(DeliveryMigrationErrors.QuotaExceeded, (await cache.MigrateAsync(command)).Error);
        Assert.Equal(rows.Select(r => r.DeliveryId), cache.ReadFailed(1, 0, 10).Select(r => r.DeliveryId));
        Assert.Null(cache.ReadMigrationReceipt(command.OperationId));
    }

    [Fact]
    public async Task OriginalWriterAndExpiryUsePolicyLeaseAndChangedRetentionAndDiskLimits()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var store = factory.Services.GetRequiredService<QueueParameterStore>();
        using var data = TemporaryDirectory.Create();
        var now = DateTimeOffset.UtcNow;
        var source = new PolicyProbe(store);
        var cache = new DeliveryCache(new DeliveryCacheOptions
        {
            DatabasePath = Path.Combine(data.Path, DeliveryCacheOptions.FileName),
            ConfigStoreId = "policy-fixture",
            CommitInterval = TimeSpan.FromMilliseconds(1),
        }, new ManualTimeProvider(now), () => new(10_000_000_000, 5_000_000_000), policy: source);
        await using var writer = new DeliveryWriterUnit(cache, TimeProvider.System, new RecordingEventSink(), NullLogger.Instance);
        using var stop = new CancellationTokenSource();
        var running = writer.RunAsync(stop.Token);
        try
        {
            var seqs = (await cache.EnqueueAsync([Draft(1), Draft(1), Draft(1)], Token)).Seqs;
            Assert.Equal(3, seqs.Count);
            await cache.RecordFailureAsync(new(seqs[1], 0, "fixture", true, false), Token);
            await cache.QuarantineAsync(seqs[2], DeliveryQuarantineReasons.TargetChanged, Token);
            Assert.True(source.Leases >= 3);
            Set(store, store.Read().Values with { MinimumDiskFreePercent = 60 });
            Assert.True(cache.StorageProtection.Status.RejectNewRecords); // Uses cached space with new threshold immediately.
            Assert.Equal(1, (await cache.EnqueueAsync([Draft(1)], Token)).StorageProtectionRejectedRecords);
            Set(store, store.Read().Values with { MinimumDiskFreePercent = 5 });
            Assert.False(cache.StorageProtection.Status.RejectNewRecords);
        }
        finally { await stop.CancelAsync(); await running.WaitAsync(TimeSpan.FromSeconds(10), Token); }
        using (var connection = cache.Store.OpenWriteConnection())
        {
            using var sql = connection.CreateCommand();
            sql.CommandText = "UPDATE delivery SET created_at=$old, failed_at=CASE WHEN failed_at IS NULL THEN NULL ELSE $old END";
            sql.Parameters.AddWithValue("$old", now.AddHours(-2).ToUnixTimeMilliseconds());
            sql.ExecuteNonQuery();
            cache.Store.ExpirePending(connection);
            cache.Store.ExpireFailures(connection);
            Assert.Single(cache.ReadPending(1, 0, 10));
            Assert.Single(cache.ReadFailed(1, 0, 10));
            Assert.Single(cache.ReadQuarantined(1, 0, 10));
            Set(store, store.Read().Values with { PendingRetentionHours = 1, FailedRetentionHours = 1 });
            var leases = source.Leases;
            cache.Store.ExpirePending(connection);
            cache.Store.ExpireFailures(connection);
            Assert.Equal(leases + 2, source.Leases);
            Assert.Empty(cache.ReadPending(1, 0, 10));
            Assert.Empty(cache.ReadFailed(1, 0, 10));
            Assert.Empty(cache.ReadQuarantined(1, 0, 10));
            Assert.Equal(2, cache.GetQueueTotals(1).Expired);
            Assert.Equal(1, cache.GetFailureTotals(1).Expired);
        }
        cache.Store.ClearReadPool();
    }

    [Fact]
    public void MigrationFrom23KeepsExistingParametersAndIdentity()
    {
        using var store = TestConfigStore.Create();
        store.Initialize(ConfigMigrations.All.Where(m => m.Version <= 23).ToArray());
        var identity = GatewayIdentity.LoadOrCreate(store.Database, out _);
        store.Database.Client.Insertable(new RuntimeParameterRecord { Version = Guid.NewGuid().ToString("N"), AuditRetentionDays = 365, PreviewGlobalLimit = 6, PreviewSessionLimit = 3 }).ExecuteCommand();
        Assert.Equal(24, store.CreateMigrator().Migrate());
        Assert.Equal(24, store.CreateMigrator().Migrate());
        Assert.Equal(identity, GatewayIdentity.LoadOrCreate(store.Database, out _));
        Assert.Empty(store.Database.Client.Queryable<QueueParameterRecord>().ToList());
        Assert.Equal(365, store.Database.Client.Queryable<RuntimeParameterRecord>().InSingle(1).AuditRetentionDays);
    }

    [Fact]
    public async Task CorruptPersistedParametersAreNotSilentlyResetByStartupLoader()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var store = factory.Services.GetRequiredService<QueueParameterStore>();
        Set(store, QueueParameterValues.Default with { MaximumPendingPerTarget = 1 });
        var database = factory.Services.GetRequiredService<ConfigDatabase>();
        database.Client.Ado.ExecuteCommand("UPDATE queue_parameters SET values_json='{}' WHERE id=1");
        var reload = new QueueParameterStore(database, factory.Services.GetRequiredService<AuditTrail>(), TimeProvider.System);
        Assert.Equal(HostExitCodes.ConfigStoreUnavailable, Assert.Throws<HostStartupException>(() => reload.Read()).ExitCode);
    }

    private sealed class PolicyProbe(QueueParameterStore store) : IDeliveryPolicySource
    {
        public int Leases { get; private set; }
        public DeliveryPolicy Read() => ((IDeliveryPolicySource)store).Read();
        public IDisposable AcquireRead()
        {
            var lease = store.AcquireRead();
            try
            {
                var state = store.Read();
                Assert.Throws<QueueParameterBusyException>(() => store.Write(new(state.Values, state.Version, true), "fixture", null));
                Leases++;
                return lease;
            }
            catch { lease.Dispose(); throw; }
        }
    }
    private static void Set(QueueParameterStore store, QueueParameterValues value) => store.Write(new(value, store.Read().Version, true), "fixture", null);
    private static DeliveryDraft Draft(long id) => new(Guid.NewGuid(), id, Guid.NewGuid(), "device", 1, "{}"u8.ToArray(), "fp", "target");
    private static Task<HttpResponseMessage> Write(HttpClient client, QueueParameterValues values, string? version) =>
        client.PutAsJsonAsync(new Uri(Endpoint, UriKind.Relative), new QueueParameterRequest(values, version, true), Token);
    private static async Task<QueueParameterState> Read(HttpClient client) => (await client.GetFromJsonAsync<QueueParameterState>(Endpoint, Token))!;
    private static Task<HttpClient> Admin(TenonEdgeApplicationFactory factory)
    {
        TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        return factory.CreateLoggedInClientAsync("admin", Token);
    }
}
