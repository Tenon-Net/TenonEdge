using System.Globalization;
using Microsoft.Data.Sqlite;

namespace TenonEdge.Runtime.Deliveries;

internal sealed partial class DeliveryStore
{
    /// <summary>只在唯一写者的当前事务中执行。只读有界元数据，不把所选正文装入托管内存。
    /// seq/稳定编号/失败状态/目标指纹、所有目标配额及全局配额全部检查后才改动。</summary>
    internal DeliveryWriteResult RetryFailed(SqliteConnection connection, SqliteTransaction transaction, RetryFailedRequest request, long now)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TEMP TABLE IF NOT EXISTS retry_selection (seq INTEGER PRIMARY KEY, delivery_id TEXT NOT NULL UNIQUE, next_seq INTEGER NOT NULL);
            DELETE FROM retry_selection;
            SELECT MAX(COALESCE((SELECT seq FROM sqlite_sequence WHERE name = 'delivery'), 0), COALESCE((SELECT MAX(seq) FROM delivery), 0));
            """;
        var previousMaximum = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        if (previousMaximum > long.MaxValue - request.Items.Count)
        { return DeliveryWriteResult.Rejected(DeliveryRetryErrors.SequenceExhausted); }
        command.CommandText = "INSERT INTO retry_selection VALUES ($seq, $id, $next)";
        command.Parameters.Add("$seq", SqliteType.Integer);
        command.Parameters.Add("$id", SqliteType.Text);
        command.Parameters.Add("$next", SqliteType.Integer);
        for (var i = 0; i < request.Items.Count; i++)
        {
            command.Parameters["$seq"].Value = request.Items[i].Sequence;
            command.Parameters["$id"].Value = request.Items[i].DeliveryId.ToString("D");
            command.Parameters["$next"].Value = previousMaximum + i + 1;
            command.ExecuteNonQuery();
        }
        command.Parameters.Clear();
        command.CommandText = """
            SELECT d.connection_id, d.fingerprint, d.byte_size
            FROM delivery d JOIN retry_selection s ON s.seq = d.seq AND s.delivery_id = d.delivery_id
            WHERE d.state = 'failed' ORDER BY d.seq;
            """;
        var totals = new Dictionary<long, (int Count, long Bytes)>();
        var selected = 0;
        var targetChanged = false;
        long selectedBytes = 0;
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var id = reader.GetInt64(0);
                var bytes = reader.GetInt64(2);
                targetChanged |= !request.TargetFingerprints.TryGetValue(id, out var fingerprint) || fingerprint != reader.GetString(1);
                var previous = totals.GetValueOrDefault(id);
                totals[id] = (previous.Count + 1, checked(previous.Bytes + bytes));
                selectedBytes = checked(selectedBytes + bytes);
                selected++;
            }
        }
        if (selected != request.Items.Count)
        { return DeliveryWriteResult.Rejected(DeliveryRetryErrors.SelectionChanged); }
        if (targetChanged)
        { return DeliveryWriteResult.Rejected(DeliveryRetryErrors.TargetChanged); }
        foreach (var (id, selectedTotal) in totals)
        {
            command.CommandText = "SELECT COUNT(*), COALESCE(SUM(byte_size), 0) FROM delivery WHERE connection_id = $target AND state = 'pending'";
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$target", id);
            using var reader = command.ExecuteReader();
            reader.Read();
            if (selectedTotal.Count > _options.MaximumPendingPerTarget - reader.GetInt64(0) ||
                selectedTotal.Bytes > _options.MaximumPendingBytesPerTarget - reader.GetInt64(1))
            { return DeliveryWriteResult.Rejected(DeliveryRetryErrors.QuotaExceeded); }
        }
        command.Parameters.Clear();
        command.CommandText = "SELECT COALESCE(SUM(byte_size), 0) FROM delivery WHERE state IN ('pending', 'quarantined')";
        var currentGlobal = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        if (selectedBytes > _options.MaximumQueueBytes - currentGlobal)
        { return DeliveryWriteResult.Rejected(DeliveryRetryErrors.QuotaExceeded); }
        command.CommandText = """
            UPDATE delivery SET seq = (SELECT next_seq FROM retry_selection WHERE seq = delivery.seq),
                state = 'pending', failure_count = 0, failed_at = NULL, last_error = NULL, quarantine_reason = NULL, last_retried_at = $now
            WHERE seq IN (SELECT seq FROM retry_selection);
            """;
        command.Parameters.AddWithValue("$now", now);
        var affected = command.ExecuteNonQuery();
        if (affected != selected)
        { throw new DeliveryStoreException("失败记录重试未更新完整选择，事务必须回滚。"); }
        // INTEGER PRIMARY KEY 的 UPDATE 不推进 AUTOINCREMENT；显式推进同事务高水位，删除后也不复用。
        command.CommandText = "UPDATE sqlite_sequence SET seq = $maximum WHERE name = 'delivery'";
        command.Parameters.AddWithValue("$maximum", previousMaximum + selected);
        if (command.ExecuteNonQuery() != 1)
        { throw new DeliveryStoreException("失败记录重试无法更新队列高水位，事务必须回滚。"); }
        AddCounter(connection, transaction, "manuallyRetriedRecords", selected);
        return new(DeliveryWriteStatus.Committed, [], selected, null);
    }
}
