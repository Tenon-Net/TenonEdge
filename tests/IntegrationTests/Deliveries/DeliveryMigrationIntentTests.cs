using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Host.Features.Audit;
using TenonEdge.Host.Features.Deliveries;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Storage;
using TenonEdge.Host.Storage.Migrations;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.Runtime.Deliveries;

namespace TenonEdge.IntegrationTests.Deliveries;

public sealed class DeliveryMigrationIntentTests
{
    private static readonly string[] Inputs = ["meter/input"];
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static AuditEntry Application => new(AuditActions.SnapshotApplied, AuditResults.Success, "admin", "192.0.2.10");
    private static string Recipient(string protocol, string host) => protocol == "http" ? $"http://{host}.test/base/" : $"mqtt://{host}.test:1883";
    private static DeliveryMigrationIntentDraft Intent(IServiceProvider services, string protocol = "http", string old = "old", string next = "new", long connection = 1) => new(
        new(services.GetRequiredService<GatewayIdentity>().ConfigStoreId, Guid.NewGuid(), connection, "fp-" + old),
        protocol == "http" ? OutputRecipientIdentity.Http(Recipient(protocol, old), null) : OutputRecipientIdentity.Mqtt(Recipient(protocol, old), false, null),
        Recipient(protocol, next), true);
    private static object Unit(string protocol, string host, string path = "send", bool queue = true, long connection = 1) => new
    {
        kind = "output." + protocol,
        key = "target-" + connection,
        configuration = protocol == "http" ? (object)new
        { connectionId = connection, baseAddress = Recipient(protocol, host), path = queue ? "" : path, queueOnly = queue, inputs = queue ? Array.Empty<string>() : Inputs }
            : new { connectionId = connection, host = host + ".test", topic = queue ? "" : path, queueOnly = queue, inputs = queue ? Array.Empty<string>() : Inputs },
    };
    private static RuntimePlanDocument Plan(params object[] units) => RuntimePlanDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, units }));
    private static DeliveryDraft Draft(long connection = 1, string fp = "fp-old") => new(Guid.NewGuid(), connection, Guid.NewGuid(), "meter", 1,
        "private-frozen-body"u8.ToArray(), fp, "original-summary", "frozen/path");

    [Theory]
    [InlineData("http", false)]
    [InlineData("mqtt", false)]
    [InlineData("http", true)]
    [InlineData("mqtt", true)]
    public async Task IntentAndAuditCommitWithEffectivePointerButDoNotPrematurelyModifyQueue(string protocol, bool queueOnly)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var services = factory.Services;
        var versions = services.GetRequiredService<RuntimeVersionStore>();
        versions.Commit(versions.Freeze(Plan(Unit(protocol, "old", queue: queueOnly)), RuntimeSnapshotOrigins.Seed, null, "system"), null);
        var cache = services.GetRequiredService<DeliveryCache>();
        await cache.EnqueueAsync([Draft()], Token);
        var next = Plan(Unit(protocol, "new", queue: queueOnly));
        var intent = Intent(services, protocol);
        Assert.NotNull(versions.TargetChangeProblem(next));
        Assert.Null(versions.TargetChangeProblem(next, [intent]));
        var revision = versions.FreezeDraft(next, "0", "admin");
        Assert.Throws<InvalidOperationException>(() => versions.Commit(revision, Application, markRunning: true));
        Assert.Equal(1, versions.ReadPointers().EffectiveRevision);
        Assert.Null(cache.ReadMigrationHold(intent.Hold.IntentId));
        Assert.False(versions.Commit(revision, Application, markRunning: true, migrationIntents: [intent]));
        Assert.Equal(revision, versions.ReadPointers().EffectiveRevision);
        Assert.Null(versions.ReadPointers().PendingRevision);
        var stored = services.GetRequiredService<DeliveryMigrationStore>().ReadIntent(intent.Hold.IntentId)!;
        Assert.Equal((revision, 1L, "waiting", "admin", "192.0.2.10"),
            (stored.Revision, stored.SourceRevision, stored.Status, stored.AccountName, stored.SourceIp));
        Assert.Equal(intent.Hold.RequestHash, stored.ToHold().RequestHash);
        Assert.Equal(intent.Source.Freeze(), stored.SourceSummary);
        Assert.Null(cache.ReadMigrationHold(intent.Hold.IntentId));
        Assert.Single(cache.ReadPending(1, 0, 10));
        var audit = Assert.Single(TestAccounts.Audit(services, "delivery.migrationIntentRegistered"));
        Assert.Contains("sameOwnerConfirmed", audit.Detail!, StringComparison.Ordinal);
        Assert.DoesNotContain("private-frozen-body", audit.Detail!, StringComparison.Ordinal);
        Assert.Single(TestAccounts.Audit(services, AuditActions.SnapshotApplied));
    }

    [Theory]
    [InlineData("path")]
    [InlineData("topic")]
    [InlineData("delete")]
    [InlineData("protocol")]
    [InlineData("sameAddress")]
    [InlineData("oldAddress")]
    [InlineData("newAddress")]
    [InlineData("owner")]
    [InlineData("foreignStore")]
    [InlineData("duplicateConnection")]
    [InlineData("unselectedTarget")]
    public async Task MigrationCannotBypassOtherConfigurationGuards(string invalid)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var services = factory.Services;
        var versions = services.GetRequiredService<RuntimeVersionStore>();
        var protocol = invalid == "topic" ? "mqtt" : "http";
        var previous = Plan(Unit(protocol, "old", queue: false), Unit(protocol, "old", queue: false, connection: 2));
        versions.Commit(versions.Freeze(previous, RuntimeSnapshotOrigins.Seed, null, "system"), null);
        await services.GetRequiredService<DeliveryCache>().EnqueueAsync([Draft(), Draft(2)], Token);
        var nextProtocol = invalid == "protocol" ? "mqtt" : protocol;
        var next = invalid == "delete" ? Plan() : Plan(Unit(nextProtocol, invalid == "sameAddress" ? "old" : "new",
            path: invalid is "path" or "topic" ? "changed" : "send", queue: false),
            Unit(protocol, invalid == "unselectedTarget" ? "new" : "old", queue: false, connection: 2));
        var intent = Intent(services, protocol);
        intent = invalid switch
        {
            "owner" => intent with { SameOwnerConfirmed = false },
            "oldAddress" => intent with { Source = intent.Source with { Address = Recipient(protocol, "different") } },
            "newAddress" => intent with { TargetRecipient = Recipient(protocol, "different") },
            "foreignStore" => intent with { Hold = new(Guid.NewGuid().ToString("D"), intent.Hold.IntentId, 1, "fp-old") },
            _ => intent,
        };
        var revision = versions.FreezeDraft(next, "0", "admin");
        DeliveryMigrationIntentDraft[] intents = invalid == "duplicateConnection" ? [intent, intent] : [intent];
        Assert.Throws<InvalidOperationException>(() => versions.Commit(revision, Application, markRunning: true, migrationIntents: intents));
        Assert.Equal(1, versions.ReadPointers().EffectiveRevision);
        Assert.Equal(revision, versions.ReadPointers().PendingRevision);
        Assert.Empty(services.GetRequiredService<DeliveryMigrationStore>().ReadWaitingIntents());
        Assert.Empty(TestAccounts.Audit(services, "delivery.migrationIntentRegistered"));
        Assert.Single(services.GetRequiredService<DeliveryCache>().ReadPending(1, 0, 10));
    }

    [Theory]
    [InlineData("seed")]
    [InlineData("noRunning")]
    [InlineData("noActor")]
    [InlineData("auditFailure")]
    [InlineData("intentAuditFailure")]
    public async Task InvalidContextOrLateAuditFailureRollsBackBothIntentAndEffectiveVersion(string boundary)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var services = factory.Services;
        var versions = services.GetRequiredService<RuntimeVersionStore>();
        versions.Commit(versions.Freeze(Plan(Unit("http", "old")), RuntimeSnapshotOrigins.Seed, null, "system"), null);
        await services.GetRequiredService<DeliveryCache>().EnqueueAsync([Draft()], Token);
        var next = Plan(Unit("http", "new"));
        var revision = boundary == "seed" ? versions.Freeze(next, RuntimeSnapshotOrigins.Seed, null, "system") : versions.FreezeDraft(next, "0", "admin");
        var intent = Intent(services);
        var database = services.GetRequiredService<ConfigDatabase>();
        if (boundary is "auditFailure" or "intentAuditFailure")
        {
            var action = boundary == "auditFailure" ? AuditActions.SnapshotApplied : "delivery.migrationIntentRegistered";
            database.Client.Ado.ExecuteCommand($"CREATE TRIGGER fail_intent_audit BEFORE INSERT ON audit_log WHEN NEW.action='{action}' BEGIN SELECT RAISE(ABORT,'injected'); END;");
        }
        Assert.ThrowsAny<Exception>(() => versions.Commit(revision, boundary == "noActor" ? Application with { AccountName = null } : Application,
            markRunning: boundary != "noRunning", migrationIntents: [intent]));
        Assert.Equal(1, versions.ReadPointers().EffectiveRevision);
        Assert.Equal(RuntimeSnapshotStates.Effective, versions.Load(1)!.State);
        Assert.Equal(RuntimeSnapshotStates.Pending, versions.Load(revision)!.State);
        Assert.Empty(services.GetRequiredService<DeliveryMigrationStore>().ReadWaitingIntents());
        Assert.Empty(TestAccounts.Audit(services, "delivery.migrationIntentRegistered"));
        Assert.Empty(TestAccounts.Audit(services, AuditActions.SnapshotApplied));
        Assert.Single(services.GetRequiredService<DeliveryCache>().ReadPending(1, 0, 10));
    }

    [Fact]
    public void VersionEighteenUpgradeKeepsIdentityAndExistingAttemptsAndDoesNotInventIntent()
    {
        using var store = TestConfigStore.Create();
        store.Initialize(ConfigMigrations.All.Where(migration => migration.Version <= 18).ToArray());
        var identity = GatewayIdentity.LoadOrCreate(store.Database, out _);
        store.Database.Client.Ado.ExecuteCommand("""
            INSERT INTO delivery_migration (operation_id, config_store_id, request_hash, mode, revision, source_connection_id,
                target_connection_id, source_summary, target_summary, account_name, started_at, status)
            VALUES ('previous-attempt', @store, 'hash', 'selected', 1, 1, 2, 'old', 'new', 'admin', 1, 'interrupted')
            """, new { store = identity.ConfigStoreId });
        store.Initialize();
        Assert.Equal(identity, GatewayIdentity.LoadOrCreate(store.Database, out var created));
        Assert.False(created);
        Assert.Equal(1, store.Database.Client.Ado.GetInt("SELECT COUNT(*) FROM delivery_migration WHERE operation_id='previous-attempt' AND status='interrupted'"));
        Assert.Equal(0, store.Database.Client.Ado.GetInt("SELECT COUNT(*) FROM delivery_migration_intent"));
        store.Initialize();
        Assert.Equal(0, store.Database.Client.Ado.GetInt("SELECT COUNT(*) FROM delivery_migration_intent"));
    }

    [Fact]
    public async Task WaitingIntentsDoNotBlockLaterApplicationsAndArePagedInRegistrationOrder()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var services = factory.Services;
        var versions = services.GetRequiredService<RuntimeVersionStore>();
        versions.Commit(versions.Freeze(Plan(Unit("http", "old")), RuntimeSnapshotOrigins.Seed, null, "system"), null);
        var first = Intent(services);
        var revision = versions.FreezeDraft(Plan(Unit("http", "new")), "0", "admin");
        versions.Commit(revision, Application, markRunning: true, migrationIntents: [first]);
        var second = Intent(services, old: "new", next: "third");
        var later = versions.FreezeDraft(Plan(Unit("http", "third")), "0", "admin");
        versions.Commit(later, Application, markRunning: true, migrationIntents: [second]);
        var store = services.GetRequiredService<DeliveryMigrationStore>();
        var page = Assert.Single(store.ReadWaitingIntents(limit: 1));
        Assert.Equal(first.Hold.IntentId.ToString("D"), page.IntentId);
        var next = Assert.Single(store.ReadWaitingIntents(page.Revision, page.ConnectionId, 1));
        Assert.Equal(second.Hold.IntentId.ToString("D"), next.IntentId);
        Assert.Empty(store.ReadWaitingIntents(next.Revision, next.ConnectionId, 1));
        Assert.Equal(later, versions.ReadPointers().EffectiveRevision);
        Assert.Equal(2, TestAccounts.Audit(services, "delivery.migrationIntentRegistered").Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartupIsolatesBeforeSendersAndStorageFailureKeepsRuntimeStoppedUntilExplicitRetry(bool failIsolation)
    {
        using var data = TemporaryDirectory.Create();
        Guid intentId;
        string hash;
        await using (var setup = new FixedDataDirectoryApplicationFactory(data.Path))
        {
            using var client = setup.CreateClient();
            var versions = setup.Services.GetRequiredService<RuntimeVersionStore>();
            versions.Commit(versions.Freeze(Plan(Unit("http", "old")), RuntimeSnapshotOrigins.Seed, null, "system"), null);
            var cache = setup.Services.GetRequiredService<DeliveryCache>();
            var seqs = (await cache.EnqueueAsync([Draft(), Draft()], Token)).Seqs;
            await cache.RecordFailureAsync(new(seqs[0], 4, "failed-before-commit", true, false), Token);
            var next = Plan(Unit("http", "new"));
            hash = next.ContentHash;
            var intent = Intent(setup.Services);
            intentId = intent.Hold.IntentId;
            versions.Commit(versions.FreezeDraft(next, "0", "admin"), Application, markRunning: true, migrationIntents: [intent]);
            versions.ClearRunningMarker(2); // Fixture persisted a commit, but never activated that plan; no fake crash evidence.
            Assert.Null(cache.ReadMigrationHold(intentId));
        }
        if (failIsolation)
        { Sql(data.Path, "CREATE TRIGGER fail_hold BEFORE INSERT ON delivery_migration_hold BEGIN SELECT abs(-9223372036854775808); END;"); }
        await using var restarted = new FixedDataDirectoryApplicationFactory(data.Path);
        using var http = restarted.CreateClient();
        var coordinator = restarted.Services.GetRequiredService<RuntimeCoordinator>();
        var queue = restarted.Services.GetRequiredService<DeliveryCache>();
        if (failIsolation)
        {
            Assert.Equal(RuntimeCoordinatorState.RunFailed, coordinator.Status.State);
            Assert.Empty(coordinator.Runtime.GetOutputSenders());
            Assert.Null(queue.ReadMigrationHold(intentId));
            Assert.Single(queue.ReadPending(1, 0, 10));
            Assert.Single(queue.ReadFailed(1, 0, 10));
            Sql(data.Path, "DROP TRIGGER fail_hold");
            var retry = coordinator.BeginRetry(2, hash, "admin", null, Token);
            await Runtime.OutputPipelineTests.UntilAsync(() => coordinator.ReadApplication(retry.Id)?.Stage is RuntimeApplicationStage.Completed or RuntimeApplicationStage.Failed);
            Assert.Equal(RuntimeApplicationStage.Completed, coordinator.ReadApplication(retry.Id)!.Stage);
        }
        Assert.Equal(RuntimeCoordinatorState.Active, coordinator.Status.State);
        Assert.Empty(queue.ReadPending(1, 0, 10));
        Assert.Empty(queue.ReadFailed(1, 0, 10));
        Assert.Equal(2, queue.ReadQuarantined(1, 0, 10).Count);
        Assert.Equal(2, queue.ReadMigrationHold(intentId)!.HeldRecords);
        Assert.Equal(1L, Scalar(data.Path, "SELECT COUNT(*) FROM delivery WHERE migration_previous_state='failed'"));
        Assert.Equal(0, queue.GetPersistentCounters().GetValueOrDefault("manuallyMigratedRecords"));
        Assert.Equal("waiting", restarted.Services.GetRequiredService<DeliveryMigrationStore>().ReadIntent(intentId)!.Status);
        Assert.Single(TestAccounts.Audit(restarted.Services, "delivery.migrationIntentRegistered"));
        Assert.Empty(TestAccounts.Audit(restarted.Services, "delivery.migrationStarted"));
    }

    private static object? Scalar(string path, string sql)
    {
        using var db = new SqliteConnection($"Data Source={Path.Combine(path, "queue.db")};Pooling=False");
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }
    private static void Sql(string path, string sql) => _ = Scalar(path, sql);
}
