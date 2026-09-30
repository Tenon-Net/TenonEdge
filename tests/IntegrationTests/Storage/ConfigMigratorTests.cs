using TenonEdge.Host.Bootstrap;
using TenonEdge.Host.Storage.Migrations;
using TenonEdge.IntegrationTests.Infrastructure;

namespace TenonEdge.IntegrationTests.Storage;

/// <summary>
/// 配置库的版本化迁移（T0-05，V-06 中“执行一次版本化迁移”）：不重复执行、不静默清空、失败整体回滚并保留可诊断状态、不支持降级。
/// 除第一组外，用测试专用的迁移定义模拟升级与失败；本程序的正式迁移定义不含测试内容。
/// </summary>
public sealed class ConfigMigratorTests
{
    private static readonly DateTimeOffset FirstRun = new(2026, 9, 30, 8, 0, 0, TimeSpan.Zero);

    /// <summary>测试用迁移的版本号接在本程序正式迁移之后。</summary>
    private static readonly int NextVersion = ConfigMigrations.All[^1].Version + 1;

    private static readonly ConfigMigration AddProbeTable = new(NextVersion, "probe_table", [
        "CREATE TABLE probe (id INTEGER NOT NULL PRIMARY KEY, value TEXT NOT NULL)",
        "INSERT INTO probe (id, value) VALUES (1, 'seed')",
    ]);

    [Fact]
    public void FreshDatabaseIsMigratedToLatestVersionAndHistoryIsRecorded()
    {
        using var store = TestConfigStore.Create();
        var time = new ManualTimeProvider(FirstRun);

        store.Initialize(timeProvider: time);

        var history = ReadHistory(store);
        Assert.Equal(ConfigMigrations.All.Select(m => (long)m.Version), history.Select(record => record.Version));
        Assert.Equal(ConfigMigrations.All.Select(m => m.Name), history.Select(record => record.Name));
        Assert.All(history, record =>
        {
            Assert.Equal(FirstRun.ToUnixTimeMilliseconds(), record.AppliedAt);
            Assert.False(string.IsNullOrWhiteSpace(record.ProgramVersion));
        });
        Assert.Equal(1, store.Database.Client.Ado.GetInt("SELECT COUNT(*) FROM system_state"));
    }

    [Fact]
    public void MigratorDoesNotReapplyMigrationsOrTouchExistingData()
    {
        using var store = TestConfigStore.Create();
        var time = new ManualTimeProvider(FirstRun);
        IReadOnlyList<ConfigMigration> migrations = [.. ConfigMigrations.All, AddProbeTable];
        store.Initialize(migrations, time);
        store.Database.Client.Ado.ExecuteCommand("INSERT INTO probe (id, value) VALUES (2, 'written-after-migration')");
        var historyBefore = ReadHistory(store);

        time.Advance(TimeSpan.FromDays(1));
        var version = store.CreateMigrator(migrations, time).Migrate();

        Assert.Equal(AddProbeTable.Version, version);
        Assert.Equal(historyBefore, ReadHistory(store));
        Assert.Equal(2, store.Database.Client.Ado.GetInt("SELECT COUNT(*) FROM probe"));
    }

    [Fact]
    public void UpgradeAppliesOnlyPendingMigrations()
    {
        using var store = TestConfigStore.Create();
        var time = new ManualTimeProvider(FirstRun);
        store.Initialize(ConfigMigrations.All, time);
        var firstRunHistory = ReadHistory(store);

        time.Advance(TimeSpan.FromDays(30));
        var version = store.CreateMigrator([.. ConfigMigrations.All, AddProbeTable], time).Migrate();

        Assert.Equal(AddProbeTable.Version, version);
        var history = ReadHistory(store);
        Assert.Equal(firstRunHistory, history.Take(firstRunHistory.Count));
        var upgrade = Assert.Single(history, record => record.Version == AddProbeTable.Version);
        Assert.Equal(time.GetUtcNow().ToUnixTimeMilliseconds(), upgrade.AppliedAt);
        Assert.Equal("seed", store.Database.Client.Ado.GetString("SELECT value FROM probe WHERE id = 1"));
    }

    [Fact]
    public void FailingMigrationRollsBackEveryPendingStepAndReportsWhichOneFailed()
    {
        using var store = TestConfigStore.Create();
        store.Initialize();
        var broken = new ConfigMigration(NextVersion + 1, "broken_step", ["CREATE TABLE broken (id INTEGER PRIMARY KEY)", "INSERT INTO missing_table VALUES (1)"]);

        var exception = Assert.Throws<HostStartupException>(() =>
            store.CreateMigrator([.. ConfigMigrations.All, AddProbeTable, broken]).Migrate());

        Assert.Equal(HostExitCodes.ConfigStoreUnavailable, exception.ExitCode);
        Assert.Contains($"{NextVersion + 1} broken_step", exception.Message, StringComparison.Ordinal);
        Assert.Contains("missing_table", exception.Message, StringComparison.Ordinal);
        Assert.Contains($"schema 版本 {ConfigMigrations.All[^1].Version}", exception.Message, StringComparison.Ordinal);

        // 同一事务中已经执行的测试迁移与失败迁移的第一条语句都已回滚，库保持迁移前的状态。
        Assert.Equal(ConfigMigrations.All.Select(m => (long)m.Version), ReadHistory(store).Select(record => record.Version));
        Assert.Equal(0, CountTables(store, "probe"));
        Assert.Equal(0, CountTables(store, "broken"));
    }

    [Fact]
    public void DatabaseMigratedByNewerProgramIsRejectedWithoutChanges()
    {
        using var store = TestConfigStore.Create();
        store.Initialize([.. ConfigMigrations.All, AddProbeTable]);
        var historyBefore = ReadHistory(store);

        var exception = Assert.Throws<HostStartupException>(() => store.CreateMigrator(ConfigMigrations.All).Migrate());

        Assert.Equal(HostExitCodes.ConfigStoreUnavailable, exception.ExitCode);
        Assert.Contains("不支持降级", exception.Message, StringComparison.Ordinal);
        Assert.Equal(historyBefore, ReadHistory(store));
    }

    [Fact]
    public void HistoryThatDoesNotMatchTheDefinitionsIsRejected()
    {
        using var store = TestConfigStore.Create();
        store.Initialize([.. ConfigMigrations.All, AddProbeTable]);

        var renamed = AddProbeTable with { Name = "renamed_after_release" };
        var exception = Assert.Throws<HostStartupException>(() =>
            store.CreateMigrator([.. ConfigMigrations.All, renamed]).Migrate());

        Assert.Equal(HostExitCodes.ConfigStoreUnavailable, exception.ExitCode);
        Assert.Contains("迁移历史与本程序的迁移定义不一致", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExistingTablesWithoutHistoryAreNeverOverwritten()
    {
        // 没有迁移历史、却已存在同名表的库：建表失败使整次迁移回滚，已有的表和数据保持原样，不被“初始化”清空。
        using var store = TestConfigStore.Create();
        using (var connection = store.Database.OpenInitializationConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "CREATE TABLE system_state (id INTEGER PRIMARY KEY, note TEXT); INSERT INTO system_state VALUES (7, 'keep-me');";
            command.ExecuteNonQuery();
        }

        var exception = Assert.Throws<HostStartupException>(() => store.Initialize());

        Assert.Equal(HostExitCodes.ConfigStoreUnavailable, exception.ExitCode);
        Assert.Equal("keep-me", store.Database.Client.Ado.GetString("SELECT note FROM system_state WHERE id = 7"));
        Assert.Equal(0, CountTables(store, SchemaMigrationRecord.TableName));
    }

    [Fact]
    public void MigrationDefinitionsMustBeNumberedContiguouslyFromOne()
    {
        using var store = TestConfigStore.Create();
        store.Initialize();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            store.CreateMigrator([.. ConfigMigrations.All, AddProbeTable with { Version = NextVersion + 3 }]).Migrate());

        Assert.Contains("连续编号", exception.Message, StringComparison.Ordinal);
    }

    private static List<HistoryEntry> ReadHistory(TestConfigStore store) =>
        [.. store.Database.Client.Queryable<SchemaMigrationRecord>()
            .OrderBy(record => record.Version)
            .ToList()
            .Select(record => new HistoryEntry(record.Version, record.Name, record.AppliedAt, record.ProgramVersion))];

    private static int CountTables(TestConfigStore store, string name) =>
        store.Database.Client.Ado.GetInt("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @name", new { name });

    private sealed record HistoryEntry(long Version, string Name, long AppliedAt, string ProgramVersion);
}
