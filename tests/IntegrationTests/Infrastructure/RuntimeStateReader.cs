using System.Globalization;
using Microsoft.Data.Sqlite;
using TenonEdge.Host.Storage;

namespace TenonEdge.IntegrationTests.Infrastructure;

/// <summary>
/// 直接读取（必要时修改）数据目录中 config.db 的运行版本元数据：宿主运行中或已退出都可以使用（WAL 下读取不阻塞宿主）。
/// 修改只用于构造故障场景，例如模拟上次非正常退出留下的“运行中”标记或损坏的快照。
/// </summary>
internal sealed class RuntimeStateReader(string dataDirectory)
{
    private string ConnectionString => new SqliteConnectionStringBuilder
    {
        DataSource = Path.Combine(dataDirectory, ConfigDatabase.FileName),
        Mode = SqliteOpenMode.ReadWrite,
        Pooling = false,
        DefaultTimeout = 30,
    }.ToString();

    public PointerState Pointers()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT effective_revision, previous_revision, pending_revision, running_revision, seed_hash, revision_floor FROM system_state WHERE id = 1;";
        using var reader = command.ExecuteReader();
        reader.Read();
        return new PointerState(
            NullableLong(reader, 0),
            NullableLong(reader, 1),
            NullableLong(reader, 2),
            NullableLong(reader, 3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetInt64(5));
    }

    public IReadOnlyList<SnapshotRow> Snapshots()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT revision, origin, origin_detail, state, not_effective_reason, is_stable, abnormal_exit_count, content_hash, content, created_by
            FROM runtime_snapshot ORDER BY revision;
            """;
        using var reader = command.ExecuteReader();
        var rows = new List<SnapshotRow>();
        while (reader.Read())
        {
            rows.Add(new SnapshotRow(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetInt64(5) == 1,
                reader.GetInt32(6),
                reader.GetString(7),
                reader.GetString(8),
                reader.GetString(9)));
        }

        return rows;
    }

    public SnapshotRow Snapshot(long revision) => Snapshots().Single(row => row.Revision == revision);

    public IReadOnlyList<(string Level, string? Target, string Message, int Count)> Events(string type)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT level, target, message, occurrence_count FROM runtime_event WHERE type = $type ORDER BY id;";
        command.Parameters.AddWithValue("$type", type);
        using var reader = command.ExecuteReader();
        var rows = new List<(string, string?, string, int)>();
        while (reader.Read())
        {
            rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3)));
        }

        return rows;
    }

    public IReadOnlyList<(string? Account, string? Target, string Result, string? Detail)> Audit(string action)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT account_name, target, result, detail FROM audit_log WHERE action = $action ORDER BY id;";
        command.Parameters.AddWithValue("$action", action);
        using var reader = command.ExecuteReader();
        var rows = new List<(string?, string?, string, string?)>();
        while (reader.Read())
        {
            rows.Add((
                reader.IsDBNull(0) ? null : reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3)));
        }

        return rows;
    }

    /// <summary>执行一条修改语句，用于构造故障场景。</summary>
    public void Execute(string sql)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>轮询直到条件成立；超时则失败并附上当前状态。</summary>
    public async Task WaitUntilAsync(Func<RuntimeStateReader, bool> condition, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition(this))
            {
                return;
            }

            await Task.Delay(50, cancellationToken);
        }

        throw new TimeoutException($"等待运行版本状态超时：{Pointers()}；{string.Join("；", Snapshots().Select(row => row with { Content = "…" }))}");
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        return connection;
    }

    private static long? NullableLong(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : Convert.ToInt64(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
}

internal sealed record PointerState(long? Effective, long? Previous, long? Pending, long? Running, string? SeedHash, long RevisionFloor);

internal sealed record SnapshotRow(
    long Revision,
    string Origin,
    string? OriginDetail,
    string State,
    string? NotEffectiveReason,
    bool IsStable,
    int AbnormalExitCount,
    string ContentHash,
    string Content,
    string CreatedBy);
