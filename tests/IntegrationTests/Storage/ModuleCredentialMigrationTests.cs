using TenonEdge.Host.Features.Credentials;
using TenonEdge.Host.Storage.Migrations;
using TenonEdge.IntegrationTests.Infrastructure;

namespace TenonEdge.IntegrationTests.Storage;

public sealed class ModuleCredentialMigrationTests
{
    [Fact]
    public void VersionThirtyThreeUpgradeAddsIndependentCredentialTableWithoutRewritingOldData()
    {
        using var store = TestConfigStore.Create();
        store.Initialize(ConfigMigrations.All.Where(migration => migration.Version <= 33).ToArray());
        var database = store.Database.Client;
        database.Ado.ExecuteCommand("CREATE TABLE upgrade_probe (value TEXT); INSERT INTO upgrade_probe VALUES ('unchanged')");
        Assert.Equal(ConfigMigrations.All[^1].Version, store.CreateMigrator().Migrate());
        Assert.Empty(database.Queryable<ModuleCredentialRecord>().ToList());
        var row = new ModuleCredentialRecord
        {
            ConnectionId = 1,
            Kind = "authwire",
            ModuleVersion = 1,
            Recipient = "sha256:fixture",
            Account = "user",
            AccountMarker = "marker",
            Version = "version",
            ProtectedSecrets = "fixture-ciphertext",
            UpdatedAt = 1,
        };
        database.Insertable(row).ExecuteCommand();
        Assert.Equal(ConfigMigrations.All[^1].Version, store.CreateMigrator().Migrate());
        Assert.Equal("unchanged", database.Ado.GetString("SELECT value FROM upgrade_probe"));
        Assert.Equal(row.ProtectedSecrets, Assert.Single(database.Queryable<ModuleCredentialRecord>().ToList()).ProtectedSecrets);
        row.ConnectionId = 0;
        Assert.ThrowsAny<Exception>(() => database.Insertable(row).ExecuteCommand());
        row.ConnectionId = 2;
        row.ModuleVersion = 0;
        Assert.ThrowsAny<Exception>(() => database.Insertable(row).ExecuteCommand());
        Assert.Single(database.Queryable<ModuleCredentialRecord>().ToList());
    }
}
