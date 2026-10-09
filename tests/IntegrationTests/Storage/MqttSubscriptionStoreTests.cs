using TenonEdge.Abstractions.Contracts;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Storage.Migrations;
using TenonEdge.IntegrationTests.Infrastructure;

namespace TenonEdge.IntegrationTests.Storage;

public sealed class MqttSubscriptionStoreTests
{
    [Fact]
    public void JournalSurvivesDatabaseReopenAndSeparatesBrokerAndClientIdentities()
    {
        using var directory = TemporaryDirectory.Create();
        var first = new MqttSessionIdentity("mqtt://localhost:1883", "same-client");
        var otherBroker = first with { Recipient = "mqtt://localhost:1884" };
        var otherClient = first with { ClientId = "different-client" };
        const string filter = "传感器/'/+/value";
        using (var database = TestConfigStore.Open(directory.Path))
        {
            database.Initialize();
            var journal = new MqttSubscriptionStore(database.Database);
            journal.Put(first, new(filter, 1, false));
            journal.Put(first, new(filter, 0, true));
            journal.Put(otherBroker, new(filter, 1, true));
            journal.Put(otherClient, new(filter, 1, false));
        }
        using (var database = TestConfigStore.Open(directory.Path))
        {
            database.Initialize();
            var journal = new MqttSubscriptionStore(database.Database);
            Assert.Equal(new(filter, 0, true), Assert.Single(journal.Read(first)));
            Assert.Equal(new(filter, 1, true), Assert.Single(journal.Read(otherBroker)));
            Assert.Equal(new(filter, 1, false), Assert.Single(journal.Read(otherClient)));
            journal.Remove(first, filter);
            journal.Remove(first, filter); // 未知 UNSUBACK 后重试删除同样幂等。
            journal.Clear(otherBroker);
            Assert.Empty(journal.Read(first));
            Assert.Empty(journal.Read(otherBroker));
            Assert.Single(journal.Read(otherClient));
        }
    }

    [Fact]
    public void InvalidUpdateCannotReplaceCommittedJournalEntry()
    {
        using var database = TestConfigStore.Create();
        database.Initialize();
        var journal = new MqttSubscriptionStore(database.Database);
        var session = new MqttSessionIdentity("mqtt://localhost:1883", "client");
        var saved = new MqttSubscriptionState("a/#", 1, false);
        journal.Put(session, saved);
        Assert.ThrowsAny<Exception>(() => journal.Put(session, saved with { Qos = 2, Confirmed = true }));
        Assert.Equal(saved, Assert.Single(journal.Read(session)));
    }

    [Fact]
    public void VersionTenUpgradeAddsJournalWithoutRewritingExistingConfiguration()
    {
        using var database = TestConfigStore.Create();
        database.Initialize(ConfigMigrations.All.Where(migration => migration.Version <= 10).ToArray());
        database.Database.Client.Ado.ExecuteCommand("CREATE TABLE upgrade_probe (value TEXT); INSERT INTO upgrade_probe VALUES ('keep')");
        Assert.Equal(ConfigMigrations.All[^1].Version, database.CreateMigrator().Migrate());
        Assert.Equal("keep", database.Database.Client.Ado.GetString("SELECT value FROM upgrade_probe"));
        Assert.Empty(new MqttSubscriptionStore(database.Database).Read(new("mqtt://localhost:1883", "client")));
    }
}
