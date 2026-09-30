using TenonEdge.Host.Bootstrap;

namespace TenonEdge.Host.Storage.Migrations;

/// <summary>
/// 配置库的版本化迁移（总设计 §2.3、§10.4，T0-05）：
/// <list type="bullet">
/// <item>迁移历史记在 <c>schema_migration</c> 表中，已执行的版本不再执行。</item>
/// <item>全部待执行的迁移在同一个事务中完成：任何一步失败都整体回滚，配置库保持迁移前的状态，
/// 失败的迁移和原因写入日志，宿主不启动。</item>
/// <item>库中出现本程序不认识的更高版本（由更新版本的程序迁移过）时拒绝启动，不支持降级；
/// 迁移历史与本程序的定义不一致时同样拒绝，不在不清楚的结构上继续写入。</item>
/// <item>迁移只做显式的建表和追加。已存在的同名表不会被覆盖：建表语句失败会使整次迁移回滚，不静默清空数据。</item>
/// </list>
/// </summary>
internal sealed partial class ConfigMigrator(
    ConfigDatabase database,
    IReadOnlyList<ConfigMigration> migrations,
    TimeProvider timeProvider,
    ILogger<ConfigMigrator> logger)
{
    private const string CreateHistoryTableSql = $"""
        CREATE TABLE IF NOT EXISTS {SchemaMigrationRecord.TableName} (
            version INTEGER NOT NULL PRIMARY KEY,
            name TEXT NOT NULL,
            applied_at INTEGER NOT NULL,
            program_version TEXT NOT NULL
        )
        """;

    private static readonly string ProgramVersion = ProductVersion.Current;

    /// <summary>本程序支持的最高 schema 版本。</summary>
    public int LatestVersion => migrations.Count == 0 ? 0 : migrations[^1].Version;

    /// <summary>执行尚未执行的迁移，返回迁移后的 schema 版本。</summary>
    /// <exception cref="HostStartupException">迁移失败、库版本高于本程序或迁移历史不一致。</exception>
    public int Migrate()
    {
        EnsureDefinitionsAreOrdered();

        using var client = database.CreateIsolatedClient();
        client.Ado.BeginTran();
        var currentVersion = 0;
        ConfigMigration? running = null;
        try
        {
            client.Ado.ExecuteCommand(CreateHistoryTableSql);
            var applied = client.Queryable<SchemaMigrationRecord>().OrderBy(record => record.Version).ToList();
            currentVersion = CheckHistory(applied);

            var pending = migrations.Where(migration => migration.Version > currentVersion).ToList();
            foreach (var migration in pending)
            {
                running = migration;
                foreach (var statement in migration.Statements)
                {
                    client.Ado.ExecuteCommand(statement);
                }

                client.Insertable(new SchemaMigrationRecord
                {
                    Version = migration.Version,
                    Name = migration.Name,
                    AppliedAt = timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
                    ProgramVersion = ProgramVersion,
                }).ExecuteCommand();
                LogMigrationApplied(logger, migration.Version, migration.Name);
            }

            client.Ado.CommitTran();

            if (pending.Count == 0)
            {
                LogUpToDate(logger, currentVersion);
            }
            else
            {
                LogMigrated(logger, currentVersion, LatestVersion, pending.Count);
            }

            return LatestVersion;
        }
        catch (HostStartupException)
        {
            client.Ado.RollbackTran();
            throw;
        }
        catch (Exception exception)
        {
            client.Ado.RollbackTran();
            var step = running is null ? "读取迁移历史" : $"执行迁移 {running.Version} {running.Name}";
            throw new HostStartupException(
                HostExitCodes.ConfigStoreUnavailable,
                $"配置库迁移失败，已整体回滚，配置库保持迁移前的 schema 版本 {currentVersion}。失败步骤：{step}；原因：{exception.Message}",
                exception);
        }
    }

    private int CheckHistory(List<SchemaMigrationRecord> applied)
    {
        var expectedVersion = 1L;
        foreach (var record in applied)
        {
            if (record.Version > LatestVersion)
            {
                throw new HostStartupException(
                    HostExitCodes.ConfigStoreUnavailable,
                    $"配置库已由更新版本的程序迁移到 schema 版本 {applied[^1].Version}，本程序最高支持 {LatestVersion}，不支持降级。" +
                    "请使用对应版本的程序，或按恢复流程处理。");
            }

            var definition = migrations.FirstOrDefault(migration => migration.Version == record.Version);
            if (record.Version != expectedVersion || definition is null || definition.Name != record.Name)
            {
                throw new HostStartupException(
                    HostExitCodes.ConfigStoreUnavailable,
                    $"配置库的迁移历史与本程序的迁移定义不一致：历史中的版本 {record.Version}（{record.Name}）" +
                    $"对应本程序的 {definition?.Name ?? "（无）"}，期望的下一个版本为 {expectedVersion}。为避免在不清楚的结构上写入，宿主不启动。");
            }

            expectedVersion++;
        }

        return applied.Count == 0 ? 0 : (int)applied[^1].Version;
    }

    private void EnsureDefinitionsAreOrdered()
    {
        for (var index = 0; index < migrations.Count; index++)
        {
            if (migrations[index].Version != index + 1)
            {
                throw new InvalidOperationException($"配置库迁移定义须从 1 开始连续编号，第 {index + 1} 个为 {migrations[index].Version}。");
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "配置库 schema 已是本程序支持的最新版本 {Version}。")]
    private static partial void LogUpToDate(ILogger logger, int version);

    [LoggerMessage(Level = LogLevel.Information, Message = "已执行配置库迁移 {Version}（{Name}），将随整次迁移一并提交。")]
    private static partial void LogMigrationApplied(ILogger logger, int version, string name);

    [LoggerMessage(Level = LogLevel.Information, Message = "配置库 schema 已从版本 {From} 迁移到 {To}，共 {Count} 个迁移。")]
    private static partial void LogMigrated(ILogger logger, int from, int to, int count);
}
