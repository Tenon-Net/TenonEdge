using System.Globalization;
using Microsoft.Data.Sqlite;

namespace TenonEdge.Runtime.Deliveries;

/// <summary>仅在唯一写者的一次事务内复用配额快照；插入和实际删除后更新，其他写操作或保存点回滚时失效。
/// 不跨事务缓存，不提前释放配额，不改变原拒绝顺序。</summary>
internal sealed class DeliveryAdmissionBudget(SqliteConnection connection, SqliteTransaction transaction)
{
    private readonly Dictionary<long, (long Count, long Bytes)> _targets = [];
    private long? _globalBytes;

    internal (long Count, long Bytes) ReadTarget(long connectionId)
    {
        if (_targets.TryGetValue(connectionId, out var usage))
        { return usage; }
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*), COALESCE(SUM(byte_size), 0) FROM delivery WHERE connection_id = $id AND state = 'pending'";
        command.Parameters.AddWithValue("$id", connectionId);
        using var reader = command.ExecuteReader();
        reader.Read();
        usage = (reader.GetInt64(0), reader.GetInt64(1));
        _targets.Add(connectionId, usage);
        return usage;
    }

    internal long ReadGlobalBytes()
    {
        if (_globalBytes is { } bytes)
        { return bytes; }
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COALESCE(SUM(byte_size), 0) FROM delivery WHERE state IN ('pending', 'quarantined')";
        _globalBytes = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        return _globalBytes.Value;
    }

    internal void Inserted(DeliveryDraft draft)
    {
        var (count, bytes) = _targets[draft.ConnectionId];
        _targets[draft.ConnectionId] = (count + 1, bytes + draft.ByteSize);
        _globalBytes += draft.ByteSize;
    }

    internal void Deleted(long connectionId, long bytes, string state)
    {
        if (state == "pending" && _targets.TryGetValue(connectionId, out var usage))
        { _targets[connectionId] = (usage.Count - 1, usage.Bytes - bytes); }
        if (state is "pending" or "quarantined")
        { _globalBytes -= bytes; }
    }

    internal void Invalidate()
    {
        _targets.Clear();
        _globalBytes = null;
    }
}
