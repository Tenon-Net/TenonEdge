using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TenonEdge.Host.Features.Audit;
using TenonEdge.Host.Features.Deliveries;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Storage;
using TenonEdge.Host.Storage.Ids;
using TenonEdge.Host.Storage.Migrations;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.Runtime.Deliveries;

namespace TenonEdge.IntegrationTests.Deliveries;

public sealed class DeliveryMigrationStoreTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartupReconcilesActualQueueCommitOrInterruptionAndNeverReplays(bool committed)
    {
        using var data = TemporaryDirectory.Create();
        Guid operation;
        using (var store = TestConfigStore.Open(data.Path))
        {
            store.Initialize();
            var identity = GatewayIdentity.LoadOrCreate(store.Database, out _);
            await using var runtime = RuntimeTesting.CreateRuntime([], data.Path, configureCache: options => new()
            { DatabasePath = options.DatabasePath, ConfigStoreId = identity.ConfigStoreId, CommitInterval = options.CommitInterval });
            runtime.StartCore();
            var cache = runtime.Deliveries;
            await cache.EnqueueAsync([Draft(), Draft()], Token);
            var records = cache.ReadPending(1, 0, 10);
            var command = Command(identity, records);
            operation = command.OperationId;
            var migrations = Create(store, identity, cache);
            migrations.Begin(command, 1, "old address; account=owner", true, "admin", "192.0.2.10");
            Assert.Equal("started", migrations.Read(operation)!.Status);
            if (committed)
            {
                await cache.DeleteAsync([records[0].Seq], Token);
                Assert.Equal(1, (await cache.MigrateAsync(command)).AffectedRecords);
            }
            await runtime.StopAsync(Token);
            // Deliberately leave only start in config.db, as at the pre/post queue COMMIT boundary.
            Assert.Empty(Audits(store, "delivery.migrationFinished"));
        }
        await using (var factory = new FixedDataDirectoryApplicationFactory(data.Path))
        {
            using var client = factory.CreateClient();
            var migration = factory.Services.GetRequiredService<DeliveryMigrationStore>().Read(operation)!;
            Assert.Equal(committed ? "completed" : "interrupted", migration.Status);
            Assert.Equal(committed ? 1 : 0, migration.AffectedRecords);
            var audit = Assert.Single(TestAccounts.Audit(factory.Services, "delivery.migrationFinished"));
            Assert.Equal(committed ? "success" : "failure", audit.Result);
            Assert.Equal("admin", audit.AccountName);
            Assert.Equal("192.0.2.10", audit.SourceIp);
            Assert.DoesNotContain("private-payload", audit.Detail ?? "", StringComparison.Ordinal);
            var cache = factory.Services.GetRequiredService<DeliveryCache>();
            Assert.Equal(committed ? 1 : 0, cache.GetPersistentCounters().GetValueOrDefault("manuallyMigratedRecords"));
            Assert.Equal(committed, cache.ReadMigrationReceipt(operation) is not null);
            Assert.Equal(RuntimeCoordinatorState.Idle, factory.Services.GetRequiredService<RuntimeCoordinator>().Status.State);
        }
        await using var reopened = new FixedDataDirectoryApplicationFactory(data.Path);
        using var second = reopened.CreateClient();
        Assert.Single(TestAccounts.Audit(reopened.Services, "delivery.migrationStarted"));
        Assert.Single(TestAccounts.Audit(reopened.Services, "delivery.migrationFinished"));
    }

    [Fact]
    public async Task DurableQueueResultWinsOverUpperLayerFailureAndResultAuditIsIdempotent()
    {
        using var store = TestConfigStore.Create();
        store.Initialize();
        var identity = GatewayIdentity.LoadOrCreate(store.Database, out _);
        await using var runtime = RuntimeTesting.CreateRuntime([], store.DataDirectoryPath, configureCache: options => new()
        { DatabasePath = options.DatabasePath, ConfigStoreId = identity.ConfigStoreId, CommitInterval = options.CommitInterval });
        runtime.StartCore();
        var cache = runtime.Deliveries;
        await cache.EnqueueAsync([Draft()], Token);
        var command = Command(identity, cache.ReadPending(1, 0, 10));
        var migrations = Create(store, identity, cache);
        migrations.Begin(command, 1, "old address", true, "admin", null);
        var result = await cache.MigrateAsync(command);
        var unknown = new DeliveryWriteResult(DeliveryWriteStatus.StorageFailed, [], 0, "do not persist secret exception");
        var row = migrations.Finish(command.OperationId, unknown);
        Assert.Equal("completed", row.Status);
        Assert.Equal(result.Migration!.MigrationId, row.MigrationId);
        Assert.Equal(1, row.AffectedRecords);
        Assert.Null(row.ErrorCode);
        migrations.Finish(command.OperationId, unknown);
        migrations.RecoverInterrupted();
        var audit = Assert.Single(Audits(store, "delivery.migrationFinished"));
        Assert.DoesNotContain(unknown.Error!, audit.Detail!, StringComparison.Ordinal);
        Assert.Equal("success", audit.Result);
    }

    [Theory]
    [InlineData("begin")]
    [InlineData("finish")]
    public async Task AuditFailureRollsBackConfigStateAndQueueCommitCanBeRecovered(string boundary)
    {
        using var store = TestConfigStore.Create();
        store.Initialize();
        var identity = GatewayIdentity.LoadOrCreate(store.Database, out _);
        await using var runtime = RuntimeTesting.CreateRuntime([], store.DataDirectoryPath, configureCache: options => new()
        { DatabasePath = options.DatabasePath, ConfigStoreId = identity.ConfigStoreId, CommitInterval = options.CommitInterval });
        runtime.StartCore();
        var cache = runtime.Deliveries;
        await cache.EnqueueAsync([Draft()], Token);
        var command = Command(identity, cache.ReadPending(1, 0, 10));
        var migrations = Create(store, identity, cache);
        var action = boundary == "begin" ? "delivery.migrationStarted" : "delivery.migrationFinished";
        store.Database.Client.Ado.ExecuteCommand($"CREATE TRIGGER fail_migration_audit BEFORE INSERT ON audit_log WHEN NEW.action='{action}' BEGIN SELECT abs(-9223372036854775808); END;");
        if (boundary == "begin")
        {
            Assert.ThrowsAny<Exception>(() => migrations.Begin(command, 1, "old", true, "admin", null));
            Assert.Null(migrations.Read(command.OperationId));
            Assert.Null(cache.ReadMigrationReceipt(command.OperationId));
            Assert.Empty(Audits(store, action));
        }
        else
        {
            migrations.Begin(command, 1, "old", true, "admin", null);
            var result = await cache.MigrateAsync(command);
            Assert.ThrowsAny<Exception>(() => migrations.Finish(command.OperationId, result));
            Assert.Equal("started", migrations.Read(command.OperationId)!.Status);
            Assert.NotNull(cache.ReadMigrationReceipt(command.OperationId));
            Assert.Empty(Audits(store, action));
        }
        store.Database.Client.Ado.ExecuteCommand("DROP TRIGGER fail_migration_audit");
        if (boundary == "begin")
        { migrations.Begin(command, 1, "old", true, "admin", null); }
        migrations.RecoverInterrupted();
        Assert.Equal(boundary == "begin" ? "interrupted" : "completed", migrations.Read(command.OperationId)!.Status);
        Assert.Single(Audits(store, "delivery.migrationStarted"));
        Assert.Single(Audits(store, "delivery.migrationFinished"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrMismatchedReceiptIsNeverReportedAsSuccessful(bool mismatched)
    {
        using var store = TestConfigStore.Create();
        store.Initialize();
        var identity = GatewayIdentity.LoadOrCreate(store.Database, out _);
        await using var runtime = RuntimeTesting.CreateRuntime([], store.DataDirectoryPath, configureCache: options => new()
        { DatabasePath = options.DatabasePath, ConfigStoreId = identity.ConfigStoreId, CommitInterval = options.CommitInterval });
        runtime.StartCore();
        var cache = runtime.Deliveries;
        await cache.EnqueueAsync([Draft()], Token);
        var command = Command(identity, cache.ReadPending(1, 0, 10));
        var migrations = Create(store, identity, cache);
        migrations.Begin(command, 1, "old", true, "admin", null);
        if (mismatched)
        {
            await cache.MigrateAsync(command);
            store.Database.Client.Ado.ExecuteCommand("UPDATE delivery_migration SET request_hash='changed'");
            Assert.Throws<DeliveryStoreException>(() => migrations.RecoverInterrupted());
        }
        else
        { Assert.Throws<DeliveryStoreException>(() => migrations.Finish(command.OperationId, new(DeliveryWriteStatus.Committed, [], 1, null))); }
        Assert.Equal("started", migrations.Read(command.OperationId)!.Status);
        Assert.Empty(Audits(store, "delivery.migrationFinished"));
    }

    [Fact]
    public void OwnershipDeclarationAndStoreScopeAreMandatoryAndRejectedErrorsAreSafe()
    {
        using var store = TestConfigStore.Create();
        store.Initialize();
        var identity = GatewayIdentity.LoadOrCreate(store.Database, out _);
        var cache = RuntimeTesting.CreateDeliveryCache(store.DataDirectoryPath, configure: options => new()
        { DatabasePath = options.DatabasePath, ConfigStoreId = identity.ConfigStoreId });
        try
        {
            var migrations = Create(store, identity, cache);
            var command = DeliveryMigrationCommand.OldTarget(identity.ConfigStoreId, Guid.NewGuid(), 1, "old", "new", "new summary");
            Assert.Throws<ArgumentException>(() => migrations.Begin(command, 1, "old", false, "admin", null));
            var foreign = DeliveryMigrationCommand.OldTarget("foreign-store", Guid.NewGuid(), 1, "old", "new", "new summary");
            Assert.Throws<ArgumentException>(() => migrations.Begin(foreign, 1, "old", true, "admin", null));
            migrations.Begin(command, 1, "old", true, "admin", null);
            Assert.ThrowsAny<Exception>(() => migrations.Begin(command, 1, "old", true, "admin", null));
            var row = migrations.Finish(command.OperationId, new(DeliveryWriteStatus.StorageFailed, [], 0, "Bearer private-secret"));
            Assert.Equal("rejected", row.Status);
            Assert.Equal("delivery.migrationExecutionFailed", row.ErrorCode);
            Assert.DoesNotContain("private-secret", Assert.Single(Audits(store, "delivery.migrationFinished")).Detail!, StringComparison.Ordinal);
            Assert.Single(Audits(store, "delivery.migrationStarted"));
        }
        finally { cache.Store.ClearReadPool(); }
    }

    [Fact]
    public void RecoveryPagesStartsWithoutLoadingHistoryOrWritingDuplicateResults()
    {
        using var store = TestConfigStore.Create();
        store.Initialize();
        var identity = GatewayIdentity.LoadOrCreate(store.Database, out _);
        var cache = RuntimeTesting.CreateDeliveryCache(store.DataDirectoryPath, configure: options => new()
        { DatabasePath = options.DatabasePath, ConfigStoreId = identity.ConfigStoreId });
        try
        {
            var migrations = Create(store, identity, cache);
            for (var i = 0; i < 103; i++)
            { migrations.Begin(DeliveryMigrationCommand.OldTarget(identity.ConfigStoreId, Guid.NewGuid(), 1, "old", "new", "target"), 1, "source", true, "admin", null); }
            migrations.RecoverInterrupted();
            migrations.RecoverInterrupted();
            Assert.Equal(103, store.Database.Client.Queryable<DeliveryMigrationRecord>().Where(row => row.Status == "interrupted" && row.AffectedRecords == 0).Count());
            Assert.Equal(103, Audits(store, "delivery.migrationFinished").Count);
        }
        finally { cache.Store.ClearReadPool(); }
    }

    [Fact]
    public void VersionSeventeenUpgradeAddsNoInventedMigrationAttempts()
    {
        using var store = TestConfigStore.Create();
        store.Initialize(ConfigMigrations.All.Where(migration => migration.Version <= 17).ToArray());
        var identity = GatewayIdentity.LoadOrCreate(store.Database, out _);
        Assert.Equal(19, store.CreateMigrator().Migrate());
        Assert.Empty(store.Database.Client.Queryable<DeliveryMigrationRecord>().ToList());
        Assert.Empty(store.Database.Client.Queryable<DeliveryMigrationIntentRecord>().ToList());
        Assert.Equal(identity, GatewayIdentity.LoadOrCreate(store.Database, out var created));
        Assert.False(created);
    }

    private static DeliveryMigrationStore Create(TestConfigStore store, GatewayIdentity identity, DeliveryCache cache)
    {
        var time = TimeProvider.System;
        var audit = new AuditTrail(store.Database, IdGeneration.CreateGenerator(store.Database, time, NullLogger.Instance), time,
            new StorageWriteFallback(time, NullLogger<StorageWriteFallback>.Instance), NullLogger<AuditTrail>.Instance);
        return new(store.Database, identity, cache, audit, time);
    }
    private static List<AuditLogRecord> Audits(TestConfigStore store, string action) => store.Database.Client.Queryable<AuditLogRecord>().Where(row => row.Action == action).ToList();
    private static DeliveryDraft Draft() => new(Guid.NewGuid(), 1, Guid.NewGuid(), "meter", 1, "private-payload"u8.ToArray(), "old", "old address", "frozen/path");
    private static DeliveryMigrationCommand Command(GatewayIdentity identity, IReadOnlyList<StoredDelivery> records) => DeliveryMigrationCommand.Selected(
        identity.ConfigStoreId, Guid.NewGuid(), 1, 2, "new", "new address; account=owner", records.Select(record => new DeliveryMigrationItem(record.Seq, record.DeliveryId, record.Fingerprint, record.MigrationId)).ToArray());
}
