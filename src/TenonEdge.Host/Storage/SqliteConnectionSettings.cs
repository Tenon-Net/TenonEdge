using System.Data;
using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace TenonEdge.Host.Storage;

/// <summary>
/// 配置库物理连接的设置（总设计 §2.3、§8.5，V-06）：显式设置 synchronous=FULL 和 busy_timeout，
/// 并回读 journal_mode 与 synchronous，不依赖原生库的编译默认值。
/// <list type="bullet">
/// <item>连接池复用物理连接，而 PRAGMA 按物理连接生效，因此以原生连接句柄识别物理连接，每个只设置一次。</item>
/// <item>SqlSugar 的 CheckConnectionExecuted 在每次检查连接时都会触发（自动关闭连接时每条命令一次，事务内也会触发），
/// 这里据此保持幂等。物理连接首次打开时尚未开始事务，设置总是在事务之外执行。</item>
/// <item>journal_mode=WAL 写在库文件中、持久生效，由启动初始化设置一次；这里只回读确认。</item>
/// </list>
/// 回读结果不符时抛出异常，这个连接上的操作随之失败，不在错误的持久化设置下继续写入。
/// </summary>
internal static class SqliteConnectionSettings
{
    public const int BusyTimeoutMilliseconds = 5000;

    public const string ExpectedJournalMode = "wal";

    /// <summary>PRAGMA synchronous 回读的是数值，FULL 为 2。</summary>
    public const long SynchronousFull = 2;

    private const string ApplySql = "PRAGMA synchronous = FULL; PRAGMA busy_timeout = 5000;";

    private static readonly ConditionalWeakTable<sqlite3, object> Configured = new();
    private static readonly object Marker = new();

    public static void Apply(IDbConnection connection)
    {
        var sqlite = (SqliteConnection)connection;
        var handle = sqlite.Handle ?? throw new InvalidOperationException("配置库连接尚未打开。");
        if (Configured.TryGetValue(handle, out _))
        {
            return;
        }

        using (var command = sqlite.CreateCommand())
        {
            command.CommandText = ApplySql;
            command.ExecuteNonQuery();
        }

        var state = Read(sqlite);
        if (!string.Equals(state.JournalMode, ExpectedJournalMode, StringComparison.OrdinalIgnoreCase)
            || state.Synchronous != SynchronousFull
            || state.BusyTimeoutMilliseconds != BusyTimeoutMilliseconds)
        {
            throw new InvalidOperationException(
                $"配置库连接设置未生效：journal_mode={state.JournalMode}，synchronous={state.Synchronous}，busy_timeout={state.BusyTimeoutMilliseconds}；" +
                $"应为 {ExpectedJournalMode}、{SynchronousFull}（FULL）、{BusyTimeoutMilliseconds}。");
        }

        Configured.AddOrUpdate(handle, Marker);
    }

    /// <summary>回读一个连接当前的设置。</summary>
    public static SqliteConnectionState Read(SqliteConnection connection) => new(
        Convert.ToString(QueryScalar(connection, "PRAGMA journal_mode;"), CultureInfo.InvariantCulture) ?? string.Empty,
        Convert.ToInt64(QueryScalar(connection, "PRAGMA synchronous;"), CultureInfo.InvariantCulture),
        Convert.ToInt64(QueryScalar(connection, "PRAGMA busy_timeout;"), CultureInfo.InvariantCulture));

    private static object? QueryScalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }
}

/// <summary>一个物理连接的持久化相关设置。</summary>
internal sealed record SqliteConnectionState(string JournalMode, long Synchronous, long BusyTimeoutMilliseconds);
