using System.Globalization;
using Microsoft.Data.Sqlite;
using TenonEdge.Host.Bootstrap;
using TenonEdge.Host.Storage.Migrations;

namespace TenonEdge.Host.Storage;

/// <summary>
/// 配置库的启动初始化（T0-05），在取得数据目录锁之后执行：
/// <list type="number">
/// <item>新库先以只有属主可读写的权限创建：SQLite 创建 -wal、-shm 附属文件时沿用主库文件的权限。</item>
/// <item>读取实际加载的 SQLite 版本与编译选项并写入日志（V-07）；低于 3.51.3 时登记运行时启动条件，不启动运行时（总设计 §2.3）。</item>
/// <item>启用 WAL 日志模式（持久保存在库文件中）。</item>
/// <item>执行版本化迁移。</item>
/// </list>
/// 配置库无法打开或迁移失败时宿主不启动，以退出码 <see cref="HostExitCodes.ConfigStoreUnavailable"/> 结束；
/// 管理账号也保存在配置库中，库不可用时管理界面同样无法使用，此时须按恢复流程处理（总设计 §8.5）。
/// </summary>
internal sealed partial class ConfigStoreInitializer(
    ConfigDatabase database,
    ConfigMigrator migrator,
    RuntimeStartConditions runtimeStartConditions,
    ILogger<ConfigStoreInitializer> logger)
{
    public const string SqliteVersionBlockerCode = "sqlite-version";

    private SqliteRuntimeInfo? _sqliteRuntime;

    /// <summary>实际加载的 SQLite 原生库信息，供系统信息显示（V-07）；只在 <see cref="Initialize"/> 之后可用。</summary>
    public SqliteRuntimeInfo SqliteRuntime => _sqliteRuntime ?? throw new InvalidOperationException("配置库尚未初始化。");

    /// <summary>迁移后的配置库 schema 版本；只在 <see cref="Initialize"/> 之后有意义。</summary>
    public int SchemaVersion { get; private set; }

    public void Initialize()
    {
        SqliteRuntimeInfo runtime;
        try
        {
            CreateDatabaseFileForOwnerOnly(database.FilePath);
            using var connection = database.OpenInitializationConnection();
            runtime = SqliteRuntimeInfo.Read(connection);
            EnableWriteAheadLogging(connection);
        }
        catch (Exception exception) when (exception is SqliteException or IOException or UnauthorizedAccessException)
        {
            throw new HostStartupException(
                HostExitCodes.ConfigStoreUnavailable,
                $"无法打开配置库“{database.FilePath}”：{exception.Message}",
                exception);
        }

        LogSqliteRuntime(
            logger,
            runtime.Version,
            runtime.MicrosoftDataSqliteVersion,
            runtime.SqlitePclRawVersion,
            runtime.CompileOptions);

        if (!runtime.MeetsMinimumVersion)
        {
            var reason = $"实际加载的 SQLite 原生库版本为 {runtime.Version}，低于要求的 {SqliteRuntimeInfo.MinimumVersion}" +
                "（3.7.0 至 3.51.2 存在 WAL 重置导致数据库损坏的缺陷），运行时不会启动。";
            runtimeStartConditions.Block(SqliteVersionBlockerCode, reason);
            LogRuntimeBlocked(logger, reason);
        }

        SchemaVersion = migrator.Migrate();
        _sqliteRuntime = runtime;
    }

    private static void CreateDatabaseFileForOwnerOnly(string path)
    {
        if (File.Exists(path))
        {
            return;
        }

        using var file = new FileStream(path, PrivateFileSystem.Options(FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None));
    }

    private static void EnableWriteAheadLogging(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode = WAL;";
        var mode = Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        if (!string.Equals(mode, SqliteConnectionSettings.ExpectedJournalMode, StringComparison.OrdinalIgnoreCase))
        {
            throw new HostStartupException(
                HostExitCodes.ConfigStoreUnavailable,
                $"配置库无法启用 WAL 日志模式，当前为 {mode}。");
        }
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "SQLite 原生库版本 {SqliteVersion}（Microsoft.Data.Sqlite {MicrosoftDataSqliteVersion}，SQLitePCLRaw {SqlitePclRawVersion}）；编译选项：{CompileOptions}")]
    private static partial void LogSqliteRuntime(
        ILogger logger,
        string sqliteVersion,
        string microsoftDataSqliteVersion,
        string sqlitePclRawVersion,
        IReadOnlyList<string> compileOptions);

    [LoggerMessage(Level = LogLevel.Error, Message = "运行时启动条件不满足：{Reason}")]
    private static partial void LogRuntimeBlocked(ILogger logger, string reason);
}
