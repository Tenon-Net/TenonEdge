using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SqlSugar;
using TenonEdge.Host.Storage;

namespace TenonEdge.IntegrationTests.Infrastructure;

/// <summary>仅测试进程：在真实配置指针 UPDATE 执行之后、COMMIT 之前固定窗口。
/// SqlSugarScope 会复制根配置，不能只改启动线程的 ScopedContext；固定依赖的私有配置字段有变就明确失败。
/// 不替换事务实现、连接设置、数据库或运行时，重启仍使用原产品 Host。</summary>
internal sealed class ConfigurationTransactionGate(string directory) : IDisposable
{
    internal const string ArmedFile = "transaction-writing.armed";
    internal const string EnteredFile = "transaction-writing.entered";
    internal const string ReleasedFile = "transaction-writing.released";

    // Commit 全程同步执行，CheckConnectionExecuted 与 OnLogExecuted 在同一线程；只借用本次命令的连接。
    private readonly ThreadLocal<SqliteConnection?> _commandConnection = new();

    public void Install(ConfigDatabase database)
    {
        var scope = database.Client as SqlSugarScope ?? throw new InvalidOperationException("配置库不再使用 SqlSugarScope，须重核事务探针。");
        var field = typeof(SqlSugarScope).GetField("_configs", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("SqlSugarScope 根配置字段已变化，须重核固定依赖的事务探针。");
        var configs = field.GetValue(scope) as List<ConnectionConfig>
            ?? throw new InvalidOperationException("SqlSugarScope 根配置类型已变化。");
        var config = configs.Single();
        if (config.DbType != DbType.Sqlite || config.ConnectionString != database.ConnectionString)
        { throw new InvalidOperationException("事务探针须使用产品的同一配置库。"); }
        var events = config.AopEvents ?? throw new InvalidOperationException("产品配置库连接设置回调不存在。");
        var check = events.CheckConnectionExecuted ?? throw new InvalidOperationException("产品配置库连接设置回调不存在。");
        var executed = events.OnLogExecuted;
        events.CheckConnectionExecuted = (connection, elapsed) =>
        {
            check(connection, elapsed); // 先保留原来的 FULL/WAL/超时设置与校验。
            _commandConnection.Value = (SqliteConnection)connection;
        };
        events.OnLogExecuted = (sql, parameters) =>
        {
            var connection = _commandConnection.Value;
            _commandConnection.Value = null;
            executed?.Invoke(sql, parameters);
            if (File.Exists(Path.Combine(directory, ArmedFile)) &&
                sql.TrimStart().StartsWith("UPDATE ", StringComparison.OrdinalIgnoreCase) &&
                sql.Contains("system_state", StringComparison.OrdinalIgnoreCase) &&
                sql.Contains("effective_revision", StringComparison.OrdinalIgnoreCase))
            { HoldAfterPointerWrite(connection ?? throw new InvalidOperationException("指针命令未经过原配置库连接回调。")); }
        };
    }

    private void HoldAfterPointerWrite(SqliteConnection connection)
    {
        // CreateCommand 按已安装 Microsoft.Data.Sqlite 的契约继承当前事务；直接回读，不经过 SqlSugar 再次触发 AOP。
        using var command = connection.CreateCommand();
        var transaction = command.Transaction
            ?? throw new InvalidOperationException("指针写入不在真实事务中，不能作为提交前中断证据。");
        command.CommandText = """
            SELECT effective_revision, previous_revision, pending_revision, running_revision,
                (SELECT state FROM runtime_snapshot WHERE revision = 1),
                (SELECT state FROM runtime_snapshot WHERE revision = 2)
            FROM system_state WHERE id = 1;
            """;
        object checkpoint;
        var settings = SqliteConnectionSettings.Read(connection);
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read())
            { throw new InvalidOperationException("写事务中缺少配置指针。"); }
            checkpoint = new
            {
                database = connection.DataSource,
                inTransaction = transaction.Connection == connection,
                effective = reader.GetInt64(0),
                previous = reader.GetInt64(1),
                pending = reader.IsDBNull(2) ? (long?)null : reader.GetInt64(2),
                running = reader.GetInt64(3),
                oldState = reader.GetString(4),
                newState = reader.GetString(5),
                journalMode = settings.JournalMode,
                synchronous = settings.Synchronous,
                busyTimeout = settings.BusyTimeoutMilliseconds,
            };
        }
        // 先关闭 reader 并原子公布完整检查点；只固定本次命令，不保留 reader 或把事务交给父进程。
        var path = Path.Combine(directory, EnteredFile);
        var json = JsonSerializer.Serialize(checkpoint);
        File.WriteAllText(path + ".tmp", json);
        File.Move(path + ".tmp", path);
        Console.WriteLine($"TEST TRANSACTION WRITING GATE ENTERED: {json}");
        var elapsed = Stopwatch.StartNew();
        while (!File.Exists(Path.Combine(directory, ReleasedFile)))
        {
            if (elapsed.Elapsed >= TimeSpan.FromSeconds(60))
            { throw new TimeoutException("测试事务写入同步点未在期限内释放。"); }
            Thread.Sleep(10);
        }
        Console.WriteLine("TEST TRANSACTION WRITING GATE RELEASED");
    }

    public void Dispose() => _commandConnection.Dispose();
}
