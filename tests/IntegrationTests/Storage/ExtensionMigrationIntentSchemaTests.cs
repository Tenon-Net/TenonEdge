using System.Text.Json;
using Microsoft.Data.Sqlite;
using SqlSugar;
using TenonEdge.Host.Bootstrap;
using TenonEdge.Host.Features.Deliveries;
using TenonEdge.Host.Storage.Migrations;
using TenonEdge.IntegrationTests.Infrastructure;

namespace TenonEdge.IntegrationTests.Storage;

public sealed class ExtensionMigrationIntentSchemaTests
{
    public static TheoryData<int> UpgradePrefixes => new(Enumerable.Range(1,
        ConfigMigrations.All.Single(migration => migration.Version == 35).Statements.Count));

    [Theory]
    [MemberData(nameof(UpgradePrefixes))]
    public void FailureAfterEachUpgradeStatementRollsBackBothTablesAndAllowsRestart(int statementCount)
    {
        using var directory = TemporaryDirectory.Create();
        (string Intents, string Attempts) before;
        using (var store = TestConfigStore.Open(directory.Path))
        {
            store.Initialize(ConfigMigrations.All.Where(migration => migration.Version <= 34).ToArray());
            using var client = store.Database.CreateIsolatedClient();
            SeedHistory(client);
            before = ReadHistory(client);
            var upgrade = ConfigMigrations.All.Single(migration => migration.Version == 35);
            var interrupted = upgrade with
            {
                Statements = [.. upgrade.Statements.Take(statementCount), "SELECT * FROM missing_upgrade_failure_probe"],
            };
            var failure = Assert.Throws<HostStartupException>(() => store.CreateMigrator(
                [.. ConfigMigrations.All.Where(migration => migration.Version <= 34), interrupted]).Migrate());
            Assert.Contains("missing_upgrade_failure_probe", failure.Message, StringComparison.Ordinal);
            Assert.Equal(before, ReadHistory(client));
            Assert.Equal(34, client.Ado.GetInt("SELECT MAX(version) FROM schema_migration"));
            Assert.Equal(0, client.Ado.GetInt("SELECT COUNT(*) FROM sqlite_master WHERE name IN ('delivery_migration_v35', 'delivery_migration_intent_v35')"));
            Assert.Empty(client.Ado.GetDataTable("PRAGMA foreign_key_check").Rows);
            Assert.Equal(275, Assert.Throws<SqliteException>(() => client.Insertable(Intent("still-old", "authwire", 4)).ExecuteCommand()).SqliteExtendedErrorCode);
        }
        using var restarted = TestConfigStore.Open(directory.Path);
        restarted.Initialize();
        using var next = restarted.Database.CreateIsolatedClient();
        Assert.Equal(before, ReadHistory(next));
        next.Insertable(Intent("after-restart", "authwire", 4)).ExecuteCommand();
        Assert.Empty(next.Ado.GetDataTable("PRAGMA foreign_key_check").Rows);
    }

    [Fact]
    public void UpgradePreservesIntentAttemptsAndConstraintsWhileAllowingExtensionProtocols()
    {
        using var store = TestConfigStore.Create();
        store.Initialize(ConfigMigrations.All.Where(migration => migration.Version <= 34).ToArray());
        using var client = store.Database.CreateIsolatedClient();
        SeedHistory(client);
        var before = ReadHistory(client);

        Assert.Equal(ConfigMigrations.All[^1].Version, store.CreateMigrator().Migrate());
        Assert.Equal(before, ReadHistory(client));
        Assert.Equal(1, client.Ado.GetInt("PRAGMA foreign_keys"));
        Assert.Empty(client.Ado.GetDataTable("PRAGMA foreign_key_check").Rows);
        Assert.Equal("delivery_migration_intent", client.Ado.GetString("SELECT \"table\" FROM pragma_foreign_key_list('delivery_migration')"));

        var extension = Intent("extension", "authwire", 4);
        client.Insertable(extension).ExecuteCommand();
        var attempt = Attempt("extension-attempt", extension.IntentId, "started");
        client.Insertable(attempt).ExecuteCommand();
        Assert.Equal(ConfigMigrations.All[^1].Version, store.CreateMigrator().Migrate());
        Assert.Equal("authwire", client.Queryable<DeliveryMigrationIntentRecord>().InSingle("extension").Protocol);
        Assert.Equal(5, client.Queryable<DeliveryMigrationRecord>().Count());

        // Protocol syntax remains bounded; the trusted module registry still decides availability.
        foreach (var protocol in new[] { "", "HTTP", "1wire", "wire/x", "wire\0x", new string('a', 65) })
        {
            extension.IntentId = "invalid";
            extension.ConnectionId = 5;
            extension.Protocol = protocol;
            Assert.Equal(275, Assert.Throws<SqliteException>(() => client.Insertable(extension).ExecuteCommand()).SqliteExtendedErrorCode);
        }
        extension.Protocol = "wire.v2-test";
        extension.Status = "invalid";
        Assert.Equal(275, Assert.Throws<SqliteException>(() => client.Insertable(extension).ExecuteCommand()).SqliteExtendedErrorCode);
        extension.Status = "waiting";
        extension.ConnectionId = 4;
        Assert.Equal(2067, Assert.Throws<SqliteException>(() => client.Insertable(extension).ExecuteCommand()).SqliteExtendedErrorCode);
        attempt.OperationId = "invalid-attempt";
        attempt.IntentId = "missing";
        Assert.Equal(787, Assert.Throws<SqliteException>(() => client.Insertable(attempt).ExecuteCommand()).SqliteExtendedErrorCode);
        attempt.IntentId = "extension";
        attempt.Mode = "invalid";
        Assert.Equal(275, Assert.Throws<SqliteException>(() => client.Insertable(attempt).ExecuteCommand()).SqliteExtendedErrorCode);
        attempt.Mode = "oldTarget";
        attempt.Status = "invalid";
        Assert.Equal(275, Assert.Throws<SqliteException>(() => client.Insertable(attempt).ExecuteCommand()).SqliteExtendedErrorCode);
        Assert.Equal(787, Assert.Throws<SqliteException>(() => client.Ado.ExecuteCommand("DELETE FROM delivery_migration_intent WHERE intent_id = 'extension'")).SqliteExtendedErrorCode);
        Assert.Empty(client.Ado.GetDataTable("PRAGMA foreign_key_check").Rows);
        Assert.Equal(["ix_delivery_migration_intent_attempt", "ix_delivery_migration_intent_order", "ix_delivery_migration_intent_waiting", "ix_delivery_migration_status_started"],
            client.Ado.SqlQuery<string>("SELECT name FROM sqlite_master WHERE type = 'index' AND name LIKE 'ix_delivery_migration%' ORDER BY name"));
    }

    private static void SeedHistory(SqlSugarClient client)
    {
        var waiting = Intent("waiting", "http", 1);
        var completed = Intent("completed", "mqtt", 2);
        completed.Status = "completed";
        completed.LastOperationId = "completed-attempt";
        var rejected = Intent("rejected", "http", 3);
        rejected.Status = "rejected";
        rejected.TargetFingerprint = null; // Older intents must not gain invented approvals.
        rejected.TargetSummary = null;
        rejected.TokenName = null;
        client.Insertable(new[] { waiting, completed, rejected }).ExecuteCommand();
        var finished = Attempt("completed-attempt", completed.IntentId, "completed");
        finished.MigrationId = 123;
        finished.AffectedRecords = 7;
        finished.CompletedAt = 300;
        var failed = Attempt("rejected-attempt", rejected.IntentId, "rejected");
        failed.AffectedRecords = 0;
        failed.ErrorCode = "delivery.migrationTargetChanged";
        failed.CompletedAt = 400;
        var selected = Attempt("selected-attempt", null, "interrupted");
        selected.Mode = "selected";
        selected.TokenName = null;
        client.Insertable(new[] { Attempt("started-attempt", waiting.IntentId, "started"), finished, failed, selected }).ExecuteCommand();
    }

    private static (string Intents, string Attempts) ReadHistory(SqlSugarClient client) =>
        (JsonSerializer.Serialize(client.Queryable<DeliveryMigrationIntentRecord>().OrderBy(row => row.IntentId).ToList()),
            JsonSerializer.Serialize(client.Queryable<DeliveryMigrationRecord>().OrderBy(row => row.OperationId).ToList()));

    private static DeliveryMigrationIntentRecord Intent(string id, string protocol, long connectionId) => new()
    {
        IntentId = id,
        ConfigStoreId = "store",
        Revision = 2,
        SourceRevision = 1,
        ConnectionId = connectionId,
        Protocol = protocol,
        OldFingerprint = "old",
        SourceSummary = "frozen-source-summary",
        TargetRecipient = "frozen-target-recipient",
        TargetFingerprint = "target",
        TargetSummary = "frozen-target-summary",
        AccountName = "administrator",
        TokenName = "intent-token-name",
        SourceIp = "192.0.2.1",
        RegisteredAt = 100,
        Status = "waiting",
    };

    private static DeliveryMigrationRecord Attempt(string id, string? intentId, string status) => new()
    {
        OperationId = id,
        IntentId = intentId,
        ConfigStoreId = "store",
        RequestHash = "request-hash",
        Mode = "oldTarget",
        Revision = 2,
        SourceConnectionId = 1,
        TargetConnectionId = 2,
        SourceSummary = "frozen-source-summary",
        TargetSummary = "frozen-target-summary",
        RequestedRecords = 7,
        AccountName = "operator",
        TokenName = "attempt-token-name",
        SourceIp = "192.0.2.2",
        StartedAt = 200,
        Status = status,
    };
}
