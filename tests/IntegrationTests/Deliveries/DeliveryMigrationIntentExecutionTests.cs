using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Adapters.Http;
using TenonEdge.Host.Features.Audit;
using TenonEdge.Host.Features.Deliveries;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Storage;
using TenonEdge.Host.Storage.Migrations;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.Runtime.Deliveries;

namespace TenonEdge.IntegrationTests.Deliveries;

public sealed class DeliveryMigrationIntentExecutionTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BeginAndFinishBindIntentAtomicallyAndStartupOnlyRetriesKnownNonCommit(bool committed)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var services = factory.Services;
        var intent = await MigrationIntentFixture.RegisterAsync(services);
        var coordinator = services.GetRequiredService<RuntimeCoordinator>();
        Assert.True((await coordinator.Runtime.HoldMigrationAsync(intent.Hold, null, Token)).Committed);
        var store = services.GetRequiredService<DeliveryMigrationStore>();
        var command = MigrationIntentFixture.Command(intent);
        store.BeginIntent(command, 2);
        Assert.Equal(command.OperationId.ToString("D"), store.ReadIntent(intent.Hold.IntentId)!.LastOperationId);
        Assert.Equal(intent.Hold.IntentId.ToString("D"), store.Read(command.OperationId)!.IntentId);
        Assert.Empty(store.ReadExecutableIntents());
        Assert.Throws<InvalidOperationException>(() => store.BeginIntent(MigrationIntentFixture.Command(intent), 2));
        if (committed)
        { Assert.Equal(2, (await coordinator.Runtime.Deliveries.MigrateAsync(command)).AffectedRecords); }
        store.RecoverInterrupted();
        Assert.Equal(committed ? "completed" : "waiting", store.ReadIntent(intent.Hold.IntentId)!.Status);
        Assert.Equal(committed ? "completed" : "interrupted", store.Read(command.OperationId)!.Status);
        Assert.Equal(committed ? 0 : 1, store.ReadExecutableIntents().Length);
        if (!committed)
        {
            var resumed = MigrationIntentFixture.Command(intent);
            store.BeginIntent(resumed, 2);
            var result = await coordinator.Runtime.Deliveries.MigrateAsync(resumed);
            store.Finish(resumed.OperationId, result);
            Assert.Equal("completed", store.ReadIntent(intent.Hold.IntentId)!.Status);
            Assert.Equal(resumed.OperationId.ToString("D"), store.ReadIntent(intent.Hold.IntentId)!.LastOperationId);
            // Late read/recovery of a previous interrupted attempt must not overwrite the newer completed intent.
            store.Finish(command.OperationId, new(DeliveryWriteStatus.StorageFailed, [], 0, "unknown"));
        }
        store.RecoverInterrupted();
        var records = coordinator.Runtime.Deliveries.ReadPending(1, 0, 10);
        Assert.Equal(2, records.Count);
        Assert.All(records, record => Assert.Equal(intent.Target.Fingerprint, record.Fingerprint));
        Assert.Equal(2, coordinator.Runtime.Deliveries.GetPersistentCounters()["manuallyMigratedRecords"]);
        Assert.Equal(committed ? 1 : 2, TestAccounts.Audit(services, "delivery.migrationFinished").Count);
    }

    [Theory]
    [InlineData("begin")]
    [InlineData("finish")]
    public async Task AuditFailureRollsBackAttemptAndIntentTogetherAndQueueReceiptStillWins(string boundary)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var services = factory.Services;
        var intent = await MigrationIntentFixture.RegisterAsync(services);
        var runtime = services.GetRequiredService<RuntimeCoordinator>().Runtime;
        await runtime.HoldMigrationAsync(intent.Hold, null, Token);
        var store = services.GetRequiredService<DeliveryMigrationStore>();
        var command = MigrationIntentFixture.Command(intent);
        var database = services.GetRequiredService<ConfigDatabase>();
        var action = boundary == "begin" ? "delivery.migrationStarted" : "delivery.migrationFinished";
        database.Client.Ado.ExecuteCommand($"CREATE TRIGGER fail_attempt BEFORE INSERT ON audit_log WHEN NEW.action='{action}' BEGIN SELECT abs(-9223372036854775808); END;");
        if (boundary == "begin")
        {
            Assert.ThrowsAny<Exception>(() => store.BeginIntent(command, 2));
            Assert.Null(store.Read(command.OperationId));
            Assert.Null(store.ReadIntent(intent.Hold.IntentId)!.LastOperationId);
            Assert.Single(store.ReadExecutableIntents());
        }
        else
        {
            store.BeginIntent(command, 2);
            var result = await runtime.Deliveries.MigrateAsync(command);
            Assert.ThrowsAny<Exception>(() => store.Finish(command.OperationId, result));
            Assert.Equal("started", store.Read(command.OperationId)!.Status);
            Assert.Equal("waiting", store.ReadIntent(intent.Hold.IntentId)!.Status);
            Assert.Empty(store.ReadExecutableIntents());
        }
        Assert.Empty(TestAccounts.Audit(services, action));
        database.Client.Ado.ExecuteCommand("DROP TRIGGER fail_attempt");
        if (boundary == "begin")
        {
            store.BeginIntent(command, 2);
            Assert.True((await runtime.Deliveries.MigrateAsync(command)).Committed);
        }
        store.RecoverInterrupted();
        Assert.Equal("completed", store.ReadIntent(intent.Hold.IntentId)!.Status);
        Assert.Equal(2, store.Read(command.OperationId)!.AffectedRecords);
        Assert.Single(TestAccounts.Audit(services, "delivery.migrationFinished"));
    }

    [Theory]
    [InlineData("fingerprint")]
    [InlineData("summary")]
    [InlineData("revision")]
    [InlineData("hold")]
    [InlineData("unlinked")]
    public async Task AttemptCannotChangeApprovedScopeOrIdentity(string changed)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var services = factory.Services;
        var intent = await MigrationIntentFixture.RegisterAsync(services);
        var hold = changed == "hold" ? new DeliveryMigrationHold(intent.Hold.ConfigStoreId, intent.Hold.IntentId, 1, "other") : intent.Hold;
        var command = DeliveryMigrationCommand.ForIntent(hold, Guid.NewGuid(), changed == "fingerprint" ? "other" : intent.Target.Fingerprint,
            changed == "summary" ? "other" : intent.Target.Summary);
        var store = services.GetRequiredService<DeliveryMigrationStore>();
        if (changed == "unlinked")
        { Assert.Throws<ArgumentException>(() => store.Begin(command, 2, intent.Source.Freeze(), true, "admin", null)); }
        else
        { Assert.Throws<InvalidOperationException>(() => store.BeginIntent(command, changed == "revision" ? 1 : 2)); }
        Assert.Null(store.ReadIntent(intent.Hold.IntentId)!.LastOperationId);
        Assert.Empty(TestAccounts.Audit(services, "delivery.migrationStarted"));
    }

    [Fact]
    public async Task RejectionIsTerminalAndDoesNotAutomaticallyRetryAfterConditionsImprove()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var services = factory.Services;
        var intent = await MigrationIntentFixture.RegisterAsync(services);
        var runtime = services.GetRequiredService<RuntimeCoordinator>().Runtime;
        await runtime.HoldMigrationAsync(intent.Hold, null, Token);
        var store = services.GetRequiredService<DeliveryMigrationStore>();
        var command = MigrationIntentFixture.Command(intent);
        store.BeginIntent(command, 2);
        store.Finish(command.OperationId, new(DeliveryWriteStatus.Rejected, [], 0, DeliveryMigrationErrors.QuotaExceeded));
        store.RecoverInterrupted();
        Assert.Equal("rejected", store.ReadIntent(intent.Hold.IntentId)!.Status);
        Assert.Empty(store.ReadExecutableIntents());
        Assert.Throws<InvalidOperationException>(() => store.BeginIntent(MigrationIntentFixture.Command(intent), 2));
        Assert.Equal(2, runtime.Deliveries.ReadQuarantined(1, 0, 10).Count);
        Assert.Null(runtime.Deliveries.ReadMigrationReceipt(command.OperationId));
    }

    [Fact]
    public async Task SameConnectionKeepsRegistrationOrderAndUnreconciledAttemptBlocksLaterIntent()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var services = factory.Services;
        var first = await MigrationIntentFixture.RegisterAsync(services, count: 0);
        var versions = services.GetRequiredService<RuntimeVersionStore>();
        var second = MigrationIntentFixture.Intent(services, first.Target.Recipient.Address, "http://third.test/base/");
        versions.Commit(versions.FreezeDraft(MigrationIntentFixture.Plan(second.TargetRecipient), "0", "admin"), MigrationIntentFixture.Application,
            markRunning: true, migrationIntents: [second]);
        var store = services.GetRequiredService<DeliveryMigrationStore>();
        Assert.Equal(first.Hold.IntentId.ToString("D"), Assert.Single(store.ReadExecutableIntents()).IntentId);
        Assert.Throws<InvalidOperationException>(() => store.BeginIntent(MigrationIntentFixture.Command(second), 3));
        var command = MigrationIntentFixture.Command(first);
        store.BeginIntent(command, 3);
        Assert.Empty(store.ReadExecutableIntents());
        store.Finish(command.OperationId, new(DeliveryWriteStatus.Rejected, [], 0, DeliveryMigrationErrors.TargetChanged));
        Assert.Equal(second.Hold.IntentId.ToString("D"), Assert.Single(store.ReadExecutableIntents()).IntentId);
        store.BeginIntent(MigrationIntentFixture.Command(second), 3);
        Assert.Empty(store.ReadExecutableIntents());
    }

    [Fact]
    public void SchemaNineteenUpgradePreservesUnreviewedIntentWithoutInventingApprovedIdentity()
    {
        using var store = TestConfigStore.Create();
        store.Initialize(ConfigMigrations.All.Where(migration => migration.Version <= 19).ToArray());
        var identity = GatewayIdentity.LoadOrCreate(store.Database, out _);
        var id = Guid.NewGuid().ToString("D");
        store.Database.Client.Ado.ExecuteCommand("""
            INSERT INTO delivery_migration_intent (intent_id, config_store_id, revision, source_revision, connection_id,
                protocol, old_fingerprint, source_summary, target_recipient, account_name, registered_at, status)
            VALUES (@id, @store, 2, 1, 1, 'http', 'old', 'old-summary', 'http://new.test/', 'admin', 1, 'waiting')
            """, new { id, store = identity.ConfigStoreId });
        Assert.Equal(20, store.CreateMigrator().Migrate());
        var intent = store.Database.Client.Queryable<DeliveryMigrationIntentRecord>().InSingle(id);
        Assert.Equal(("waiting", "old", "http://new.test/", "admin"), (intent.Status, intent.OldFingerprint, intent.TargetRecipient, intent.AccountName));
        Assert.Null(intent.TargetFingerprint);
        Assert.Null(intent.TargetSummary);
        Assert.Null(intent.LastOperationId);
        Assert.Equal(identity, GatewayIdentity.LoadOrCreate(store.Database, out _));
        Assert.Equal(20, store.CreateMigrator().Migrate());
        Assert.Single(store.Database.Client.Queryable<DeliveryMigrationIntentRecord>().ToList());
    }

    [Fact]
    public async Task ExecutablePaginationDoesNotLoseOtherConnectionsBehindWaitingFirstPage()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var services = factory.Services;
        var intents = Enumerable.Range(1, 103).Select(id => MigrationIntentFixture.Intent(services, "http://old.test/base/", "http://new.test/base/", id)).ToArray();
        static RuntimePlanDocument Plan(string host) => RuntimePlanDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            units = Enumerable.Range(1, 103).Select(id => new
            {
                kind = "output.http",
                key = "queue-" + id,
                configuration = new { connectionId = id, baseAddress = $"http://{host}.test/base/", path = "", queueOnly = true, inputs = Array.Empty<string>() }
            }),
        }));
        var versions = services.GetRequiredService<RuntimeVersionStore>();
        versions.Commit(versions.Freeze(Plan("old"), RuntimeSnapshotOrigins.Seed, null, "system"), null);
        versions.Commit(versions.FreezeDraft(Plan("new"), "0", "admin"), MigrationIntentFixture.Application, markRunning: true, migrationIntents: intents);
        var store = services.GetRequiredService<DeliveryMigrationStore>();
        var first = store.ReadExecutableIntents();
        Assert.Equal(100, first.Length);
        Assert.Equal(Enumerable.Range(1, 100).Select(id => (long)id), first.Select(item => item.ConnectionId));
        var last = store.ReadExecutableIntents(first[^1].Revision, first[^1].ConnectionId);
        Assert.Equal(new long[] { 101, 102, 103 }, last.Select(item => item.ConnectionId));
        Assert.Empty(store.ReadExecutableIntents(last[^1].Revision, last[^1].ConnectionId));
        store.BeginIntent(MigrationIntentFixture.Command(intents[0]), 2);
        Assert.Equal(2, store.ReadExecutableIntents()[0].ConnectionId); // Started first connection does not block other connections.
    }

}

internal static class MigrationIntentFixture
{
    internal static AuditEntry Application => new(AuditActions.SnapshotApplied, AuditResults.Success, "admin", "192.0.2.10");
    internal static RuntimePlanDocument Plan(string address, long connection = 1) => RuntimePlanDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(new
    {
        schemaVersion = 1,
        units = new[] { new { kind = "output.http", key = "queue", configuration = new { connectionId = connection, baseAddress = address,
            path = "", queueOnly = true, inputs = Array.Empty<string>() } } },
    }));
    internal static OutputTargetIdentity Identity(string address, long connection = 1) => new(
        new HttpOutputSettings { ConnectionId = connection, BaseAddress = address, QueueOnly = true }.Fingerprint(null), OutputRecipientIdentity.Http(address, null));
    internal static DeliveryMigrationIntentDraft Intent(IServiceProvider services, string oldAddress, string address, long connection = 1) => new(
        new(services.GetRequiredService<GatewayIdentity>().ConfigStoreId, Guid.NewGuid(), connection, Identity(oldAddress, connection).Fingerprint),
        Identity(oldAddress, connection).Recipient, address, true, Identity(address, connection));
    internal static DeliveryMigrationCommand Command(DeliveryMigrationIntentDraft intent) => DeliveryMigrationCommand.ForIntent(intent.Hold, Guid.NewGuid(), intent.Target.Fingerprint, intent.Target.Summary);
    internal static async Task<DeliveryMigrationIntentDraft> RegisterAsync(IServiceProvider services, string address = "http://new.test/base/", int count = 2)
    {
        const string old = "http://old.test/base/";
        var versions = services.GetRequiredService<RuntimeVersionStore>();
        versions.Commit(versions.Freeze(Plan(old), RuntimeSnapshotOrigins.Seed, null, "system"), null);
        var intent = Intent(services, old, address);
        if (count > 0)
        {
            var cache = services.GetRequiredService<DeliveryCache>();
            var records = Enumerable.Range(0, count).Select(i =>
            {
                var id = Guid.NewGuid();
                return new DeliveryDraft(id, 1, Guid.NewGuid(), "meter", 1,
                    JsonSerializer.SerializeToUtf8Bytes(new { value = i }), intent.Hold.OldFingerprint, intent.Source.Freeze(), "send",
                    headers: new Dictionary<string, string> { ["Idempotency-Key"] = id.ToString("D") },
                    protocolMetadata: HttpFrozenRequest.Freeze(new HttpOutputSettings { Path = "send" }));
            }).ToArray();
            Assert.Equal(count, (await cache.EnqueueAsync(records, TestContext.Current.CancellationToken)).Seqs.Count);
        }
        versions.Commit(versions.FreezeDraft(Plan(address), "0", "admin"), Application, markRunning: true, migrationIntents: [intent]);
        versions.ClearRunningMarker(2); // Preparation fixture never activated this plan; not crash evidence.
        return intent;
    }
}
