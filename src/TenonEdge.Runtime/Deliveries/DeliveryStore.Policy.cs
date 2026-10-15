using System.Globalization;
using Microsoft.Data.Sqlite;

namespace TenonEdge.Runtime.Deliveries;

internal sealed partial class DeliveryStore
{
    // 单次 SQLite 快照：失败重试可能在两次查询之间从 failed 移到 pending，不能分开判断。
    internal bool HasUndelivered(long connectionId)
    {
        EnsureInitialized();
        using var connection = Open(_readConnectionString);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM delivery WHERE connection_id = $id AND state IN ('pending', 'failed'))";
        command.Parameters.AddWithValue("$id", connectionId);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 0;
    }

    public DeliveryQueueTotals ReadQueueTotals(long connectionId)
    {
        EnsureInitialized();
        using var connection = Open(_readConnectionString);
        using var transaction = connection.BeginTransaction(deferred: true);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$id", connectionId);
        command.CommandText = """
            SELECT COALESCE((SELECT quota_rejected FROM delivery_queue_totals WHERE connection_id = $id), 0),
                COALESCE((SELECT expired FROM delivery_queue_totals WHERE connection_id = $id), 0),
                COALESCE((SELECT quarantined FROM delivery_queue_totals WHERE connection_id = $id), 0),
                COUNT(*) FILTER (WHERE state = 'pending'), COALESCE(SUM(byte_size) FILTER (WHERE state = 'pending'), 0),
                COUNT(*) FILTER (WHERE state = 'quarantined'), COALESCE(SUM(byte_size) FILTER (WHERE state = 'quarantined'), 0),
                COUNT(*) FILTER (WHERE clock_anomaly = 1)
            FROM delivery WHERE connection_id = $id;
            """;
        using var reader = command.ExecuteReader();
        reader.Read();
        return new(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4),
            reader.GetInt64(5), reader.GetInt64(6), reader.GetInt64(7));
    }

    internal bool Admit(SqliteConnection connection, SqliteTransaction transaction, DeliveryDraft draft)
    {
        var policy = _policy.Read();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*), COALESCE(SUM(byte_size), 0) FROM delivery WHERE connection_id = $id AND state = 'pending'";
        command.Parameters.AddWithValue("$id", draft.ConnectionId);
        long count;
        long bytes;
        using (var reader = command.ExecuteReader())
        {
            reader.Read();
            count = reader.GetInt64(0);
            bytes = reader.GetInt64(1);
        }

        command.CommandText = "SELECT COALESCE(SUM(byte_size), 0) FROM delivery WHERE state IN ('pending', 'quarantined')";
        var global = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        var globalFull = draft.ByteSize > policy.MaximumQueueBytes - global;
        if (!globalFull && count < policy.MaximumPendingPerTarget && draft.ByteSize <= policy.MaximumPendingBytesPerTarget - bytes)
        {
            return true;
        }

        AddQueueTotals(connection, transaction, draft.ConnectionId, 1, 0);
        // 既有目标汇总仍包含所有配额拒绝；新增分类互斥，同时触发时归到全局限制。
        AddCounter(connection, transaction, globalFull ? "globalQuotaRejectedRecords"
            : $"target/{draft.ConnectionId}/quotaRejectedRecords", 1);
        return false;
    }

    internal static int QuarantineWhere(SqliteConnection connection, SqliteTransaction transaction, string predicate, string reason,
        params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$reason", reason);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        // 只接收本模块固定 SQL 条件；值均经参数传递。不把所有受影响编号读入内存。
        command.CommandText = $"""
            INSERT INTO delivery_queue_totals (connection_id, quarantined)
            SELECT connection_id, COUNT(*) FROM delivery WHERE state <> 'quarantined' AND ({predicate}) GROUP BY connection_id
            ON CONFLICT(connection_id) DO UPDATE SET quarantined = quarantined + excluded.quarantined;
            UPDATE delivery SET state = 'quarantined', quarantine_reason = $reason
            WHERE state <> 'quarantined' AND ({predicate});
            """;
        return command.ExecuteNonQuery();
    }

    internal static void Reconcile(SqliteConnection connection, SqliteTransaction transaction, ReconcileRequest request)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "CREATE TEMP TABLE IF NOT EXISTS current_targets (id INTEGER PRIMARY KEY, fingerprint TEXT NOT NULL); DELETE FROM current_targets;";
        command.ExecuteNonQuery();
        command.CommandText = "INSERT INTO current_targets VALUES ($id, $fp)";
        command.Parameters.Add(new SqliteParameter("$id", SqliteType.Integer));
        command.Parameters.Add(new SqliteParameter("$fp", SqliteType.Text));
        foreach (var (id, fingerprint) in request.Targets)
        {
            command.Parameters["$id"].Value = id;
            command.Parameters["$fp"].Value = fingerprint;
            command.ExecuteNonQuery();
        }

        QuarantineWhere(connection, transaction,
            "EXISTS (SELECT 1 FROM current_targets t WHERE t.id = delivery.connection_id AND t.fingerprint <> delivery.fingerprint)",
            DeliveryQuarantineReasons.TargetChanged);
        if (request.QuarantineMissing)
        {
            QuarantineWhere(connection, transaction,
                "NOT EXISTS (SELECT 1 FROM current_targets t WHERE t.id = delivery.connection_id)", DeliveryQuarantineReasons.TargetMissing);
        }
    }

    internal void ExpirePending(SqliteConnection connection)
    {
        using var policyLease = _policy.AcquireRead();
        var policy = _policy.Read();
        var now = _time.GetUtcNow();
        if ((now - (_maintenanceStart + _time.GetElapsedTime(_maintenanceTimestamp))).Duration() > TimeSpan.FromHours(1))
        {
            return;
        }

        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // 启动时相差一年以上，或创建/入失败区时间在未来，不能据此自动清理。标记持久保留供诊断。
        command.CommandText = """
            UPDATE delivery SET clock_anomaly = 1 WHERE clock_anomaly = 0 AND
                (created_at > $now OR failed_at > $now OR last_retried_at > $now OR created_at < $oldest);
            """;
        command.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$oldest", (_maintenanceStart - TimeSpan.FromDays(365)).ToUnixTimeMilliseconds());
        command.ExecuteNonQuery();
        command.CommandText = """
            DELETE FROM delivery WHERE seq IN (
                SELECT seq FROM delivery WHERE state IN ('pending', 'quarantined') AND clock_anomaly = 0
                AND ((failed_at IS NULL AND COALESCE(last_retried_at, created_at) <= $pendingCutoff)
                    OR (failed_at IS NOT NULL AND failed_at >= created_at AND failed_at <= $failedCutoff))
                ORDER BY seq LIMIT 100
            ) RETURNING connection_id;
            """;
        command.Parameters.AddWithValue("$pendingCutoff", now.ToUnixTimeMilliseconds() - (long)policy.PendingRetention.TotalMilliseconds);
        command.Parameters.AddWithValue("$failedCutoff", now.ToUnixTimeMilliseconds() - (long)policy.FailedRetention.TotalMilliseconds);
        var counts = new Dictionary<long, int>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var id = reader.GetInt64(0);
                counts[id] = counts.GetValueOrDefault(id) + 1;
            }
        }

        foreach (var (id, count) in counts)
        {
            AddQueueTotals(connection, transaction, id, 0, count);
        }

        transaction.Commit();
    }

    private static void AddQueueTotals(SqliteConnection connection, SqliteTransaction transaction, long id, int rejected, int expired)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO delivery_queue_totals (connection_id, quota_rejected, expired) VALUES ($id, $rejected, $expired)
            ON CONFLICT(connection_id) DO UPDATE SET quota_rejected = quota_rejected + excluded.quota_rejected, expired = expired + excluded.expired;
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$rejected", rejected);
        command.Parameters.AddWithValue("$expired", expired);
        command.ExecuteNonQuery();
    }
}
