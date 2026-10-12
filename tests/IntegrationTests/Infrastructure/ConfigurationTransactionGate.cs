using System.Data;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SqlSugar;
using TenonEdge.Host.Features.Audit;
using TenonEdge.Host.Storage;

namespace TenonEdge.IntegrationTests.Infrastructure;

/// <summary>仅测试进程：在真实配置指针 UPDATE 后，或真实 COMMIT 后而上层调用未返回时固定窗口。
/// SqlSugarScope 会复制根配置，不能只改启动线程的 ScopedContext；固定依赖的私有配置字段有变就明确失败。
/// 不替换事务实现、连接设置、数据库或运行时，重启仍使用原产品 Host。</summary>
internal sealed class ConfigurationTransactionGate(string directory, bool afterCommit = false) : IDisposable
{
    internal const string ArmedFile = "transaction-writing.armed";
    internal const string EnteredFile = "transaction-writing.entered";
    internal const string ReleasedFile = "transaction-writing.released";
    internal const string CommittedArmedFile = "durable-commit.armed";
    internal const string CommittedEnteredFile = "durable-commit.entered";
    internal const string CommittedReleasedFile = "durable-commit.released";

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
        if (config.DbType != SqlSugar.DbType.Sqlite || config.ConnectionString != database.ConnectionString)
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
            if (File.Exists(Path.Combine(directory, afterCommit ? CommittedArmedFile : ArmedFile)) &&
                sql.TrimStart().StartsWith("UPDATE ", StringComparison.OrdinalIgnoreCase) &&
                sql.Contains("system_state", StringComparison.OrdinalIgnoreCase) &&
                sql.Contains("effective_revision", StringComparison.OrdinalIgnoreCase))
            {
                var writing = connection ?? throw new InvalidOperationException("指针命令未经过原配置库连接回调。");
                if (afterCommit)
                { ObserveCommitClose(writing); }
                else
                { HoldAfterPointerWrite(writing); }
            }
        };
    }

    private void ObserveCommitClose(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        if (command.Transaction?.Connection != connection)
        { throw new InvalidOperationException("提交后探针必须先观察到真实指针写事务。"); }
        var settings = SqliteConnectionSettings.Read(connection);
        var connectionString = new SqliteConnectionStringBuilder(connection.ConnectionString)
        { Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString();
        StateChangeEventHandler? closed = null;
        closed = (_, change) =>
        {
            if (change.OriginalState != ConnectionState.Open || change.CurrentState != ConnectionState.Closed)
            { return; }
            connection.StateChange -= closed;
            // 固定 SqlSugar 5.1.4.221 的 CommitTran 同步执行 Transaction.Commit -> Close。
            // Sqlite 10.0.12 的 Close 同步发出 StateChange；本回调未返回时 CommitTran/上层 Commit 均未返回。
            // 不包装/替代 COMMIT，不重新打开原连接；独立只读连接证明事务结果已对外可见。
            string json;
            using (var observer = new SqliteConnection(connectionString))
            {
                observer.Open();
                using var read = observer.CreateCommand();
                read.CommandText = """
                    SELECT effective_revision, previous_revision, pending_revision, running_revision,
                        (SELECT state FROM runtime_snapshot WHERE revision = 1),
                        (SELECT state FROM runtime_snapshot WHERE revision = 2),
                        (SELECT count(*) FROM audit_log WHERE action = $action AND target = 'r2')
                    FROM system_state WHERE id = 1;
                    """;
                read.Parameters.AddWithValue("$action", AuditActions.SnapshotApplied);
                using var reader = read.ExecuteReader();
                if (!reader.Read() || reader.GetInt64(0) != 2 || reader.GetInt64(1) != 1 || !reader.IsDBNull(2) ||
                    reader.GetInt64(3) != 2 || reader.GetString(4) != RuntimeSnapshotStates.History ||
                    reader.GetString(5) != RuntimeSnapshotStates.Effective || reader.GetInt64(6) != 1)
                { throw new InvalidOperationException("原连接关闭时未独立读到完整已提交 r2；不能算持久 COMMIT 窗口。"); }
                json = JsonSerializer.Serialize(new
                {
                    database = observer.DataSource,
                    inTransaction = read.Transaction is not null,
                    originalConnectionClosed = connection.State == ConnectionState.Closed,
                    effective = reader.GetInt64(0),
                    previous = reader.GetInt64(1),
                    pending = (long?)null,
                    running = reader.GetInt64(3),
                    oldState = reader.GetString(4),
                    newState = reader.GetString(5),
                    appliedAuditCount = reader.GetInt64(6),
                    journalMode = settings.JournalMode,
                    synchronous = settings.Synchronous,
                    busyTimeout = settings.BusyTimeoutMilliseconds,
                });
            }
            HoldCheckpoint(json);
        };
        connection.StateChange += closed;
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
        var json = JsonSerializer.Serialize(checkpoint);
        HoldCheckpoint(json);
    }

    private void HoldCheckpoint(string json)
    {
        var path = Path.Combine(directory, afterCommit ? CommittedEnteredFile : EnteredFile);
        File.WriteAllText(path + ".tmp", json);
        File.Move(path + ".tmp", path);
        var label = afterCommit ? "DURABLE COMMIT" : "TRANSACTION WRITING";
        Console.WriteLine($"TEST {label} GATE ENTERED: {json}");
        var elapsed = Stopwatch.StartNew();
        while (!File.Exists(Path.Combine(directory, afterCommit ? CommittedReleasedFile : ReleasedFile)))
        {
            if (elapsed.Elapsed >= TimeSpan.FromSeconds(60))
            { throw new TimeoutException($"测试 {label} 同步点未在期限内释放。"); }
            Thread.Sleep(10);
        }
        Console.WriteLine($"TEST {label} GATE RELEASED");
    }

    public void Dispose() => _commandConnection.Dispose();
}
