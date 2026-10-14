using TenonEdge.Host.Features.Deliveries;
using TenonEdge.Host.Storage;
using TenonEdge.Host.Storage.Migrations;
using TenonEdge.IntegrationTests.Infrastructure;

namespace TenonEdge.IntegrationTests.Storage;

public sealed class TokenAuditMigrationTests
{
    [Fact]
    public void VersionTwentyUpgradePreservesAttemptsAndIntentIdentityWithoutInventingAToken()
    {
        using var store = TestConfigStore.Create();
        store.Initialize(ConfigMigrations.All.Where(migration => migration.Version <= 20).ToArray());
        var identity = GatewayIdentity.LoadOrCreate(store.Database, out _);
        var intentId = Guid.NewGuid().ToString("D");
        var operationId = Guid.NewGuid().ToString("D");
        store.Database.Client.Ado.ExecuteCommand("""
            INSERT INTO delivery_migration_intent (intent_id, config_store_id, revision, source_revision, connection_id,
                protocol, old_fingerprint, source_summary, target_recipient, account_name, source_ip, registered_at, status,
                last_operation_id, target_fingerprint, target_summary)
            VALUES (@intentId, @configStoreId, 2, 1, 1, 'http', 'old', 'source-summary', 'http://new.test/',
                'original', '192.0.2.10', 100, 'waiting', @operationId, 'new', 'target-summary')
            """, new { intentId, operationId, configStoreId = identity.ConfigStoreId });
        store.Database.Client.Ado.ExecuteCommand("""
            INSERT INTO delivery_migration (operation_id, config_store_id, intent_id, request_hash, mode, revision,
                source_connection_id, target_connection_id, source_summary, target_summary, account_name, source_ip,
                started_at, status)
            VALUES (@operationId, @configStoreId, @intentId, 'request-hash', 'oldTarget', 2, 1, 1,
                'source-summary', 'target-summary', 'retry-operator', '192.0.2.22', 200, 'started')
            """, new { intentId, operationId, configStoreId = identity.ConfigStoreId });

        Assert.Equal(ConfigMigrations.All[^1].Version, store.CreateMigrator().Migrate());
        Assert.Equal(ConfigMigrations.All[^1].Version, store.CreateMigrator().Migrate());
        Assert.Equal(identity, GatewayIdentity.LoadOrCreate(store.Database, out _));
        var intent = Assert.Single(store.Database.Client.Queryable<DeliveryMigrationIntentRecord>().ToList());
        Assert.Null(intent.TokenName);
        Assert.Equal(("original", "192.0.2.10", "waiting", operationId, "new", "target-summary"),
            (intent.AccountName, intent.SourceIp, intent.Status, intent.LastOperationId, intent.TargetFingerprint, intent.TargetSummary));
        var attempt = Assert.Single(store.Database.Client.Queryable<DeliveryMigrationRecord>().ToList());
        Assert.Null(attempt.TokenName);
        Assert.Equal(("retry-operator", "192.0.2.22", "started", intentId, "request-hash"),
            (attempt.AccountName, attempt.SourceIp, attempt.Status, attempt.IntentId, attempt.RequestHash));
    }
}
