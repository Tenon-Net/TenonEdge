using System.Globalization;
using Microsoft.Data.Sqlite;

namespace TenonEdge.Runtime.Deliveries;

internal sealed partial class DeliveryStore
{
    private readonly DateTimeOffset _maintenanceStart;
    private readonly long _maintenanceTimestamp;

    public DeliveryFailureTotals ReadFailureTotals(long connectionId)
    {
        EnsureInitialized();
        using var connection = Open(_readConnectionString);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT rejected, poisoned, overflowed, expired FROM delivery_failure_totals WHERE connection_id = $id";
        command.Parameters.AddWithValue("$id", connectionId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? new(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3)) : new(0, 0, 0, 0);
    }

    /// <summary>状态、失败次数、计数与满额删除在同一写者事务内完成；重复结果不重复计数。</summary>
    internal DeliveryWriteResult ApplyFailure(SqliteConnection connection, SqliteTransaction transaction, FailureRequest request, long now)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT connection_id, COALESCE(length(CAST(response_summary AS BLOB)), 0) FROM delivery WHERE seq = $seq AND state = 'pending'";
        command.Parameters.AddWithValue("$seq", request.Seq);
        long connectionId, oldSummaryBytes;
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read())
            { return DeliveryWriteResult.Deleted(0); }
            connectionId = reader.GetInt64(0);
            oldSummaryBytes = reader.GetInt64(1);
        }
        var summary = request.ResponseSummaryJson;
        var delta = request.ResponseSummaryBytes - oldSummaryBytes;
        var error = request.Error;
        command.Parameters.AddWithValue("$id", connectionId);
        if (!request.Terminal && delta > 0)
        {
            // 可选诊断不能把既有pending记录推过目标/全局字节配额，也不能阻止真实失败结果落库。
            // failed不挤占pending/全局配额；其条数本身有界。每条摘要的序列化字节仍计入byte_size。
            command.CommandText = """
                SELECT COALESCE(SUM(byte_size) FILTER (WHERE connection_id=$id AND state='pending'), 0),
                    COALESCE(SUM(byte_size) FILTER (WHERE state IN ('pending', 'quarantined')), 0) FROM delivery;
                """;
            using var reader = command.ExecuteReader();
            reader.Read();
            if (delta > _options.MaximumPendingBytesPerTarget - reader.GetInt64(0) || delta > _options.MaximumQueueBytes - reader.GetInt64(1))
            {
                summary = null;
                delta = -oldSummaryBytes;
                error = error[..Math.Min(error.Length, 900)] + "（响应摘要未保存：队列字节配额不足。）";
            }
        }

        var overflow = false;
        if (request.Terminal)
        {
            command.CommandText = "SELECT COUNT(*) FROM delivery WHERE state = 'failed' AND connection_id = $id";
            var targetCount = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
            command.CommandText = "SELECT COUNT(*) FROM delivery WHERE state = 'failed'";
            var globalCount = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
            overflow = targetCount >= _options.MaximumFailedPerTarget || globalCount >= _options.MaximumFailedGlobal;
        }

        command.CommandText = overflow ? "DELETE FROM delivery WHERE seq = $seq AND state = 'pending'" : """
            UPDATE delivery SET failure_count = MAX(failure_count, $count), last_error = $error,
                state = $state, failed_at = $at, response_summary = $summary, byte_size = byte_size + $summaryDelta
            WHERE seq = $seq AND state = 'pending';
            """;
        command.Parameters.AddWithValue("$count", request.FailureCount);
        command.Parameters.AddWithValue("$error", error);
        command.Parameters.AddWithValue("$summary", (object?)summary ?? DBNull.Value);
        command.Parameters.AddWithValue("$summaryDelta", delta);
        command.Parameters.AddWithValue("$state", request.Terminal ? "failed" : "pending");
        command.Parameters.AddWithValue("$at", request.Terminal ? now : DBNull.Value);
        var changed = command.ExecuteNonQuery();
        if (request.Terminal && changed > 0)
        {
            AddTotals(connection, transaction, connectionId, request.Poison ? 0 : 1, request.Poison ? 1 : 0, overflow ? 1 : 0, 0);
        }

        return DeliveryWriteResult.Deleted(changed);
    }

    /// <summary>每轮最多清理 100 条；墙钟明显跳变时保守保留，计数和删除同事务提交。</summary>
    internal void ExpireFailures(SqliteConnection connection)
    {
        var now = _time.GetUtcNow();
        var expected = _maintenanceStart + _time.GetElapsedTime(_maintenanceTimestamp);
        if ((now - expected).Duration() > TimeSpan.FromHours(1))
        {
            return;
        }

        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM delivery WHERE seq IN (
                SELECT seq FROM delivery WHERE state = 'failed' AND clock_anomaly = 0 AND failed_at <= $cutoff
                    AND failed_at >= created_at
                ORDER BY failed_at LIMIT 100
            ) RETURNING connection_id;
            """;
        command.Parameters.AddWithValue("$cutoff", now.ToUnixTimeMilliseconds() - (long)_options.FailedRetention.TotalMilliseconds);
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
            AddTotals(connection, transaction, id, 0, 0, 0, count);
        }

        transaction.Commit();
    }

    private static void AddTotals(SqliteConnection connection, SqliteTransaction transaction, long id, int rejected, int poisoned, int overflowed, int expired)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO delivery_failure_totals (connection_id, rejected, poisoned, overflowed, expired)
            VALUES ($id, $rejected, $poisoned, $overflowed, $expired)
            ON CONFLICT(connection_id) DO UPDATE SET
                rejected = rejected + excluded.rejected, poisoned = poisoned + excluded.poisoned,
                overflowed = overflowed + excluded.overflowed, expired = expired + excluded.expired;
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$rejected", rejected);
        command.Parameters.AddWithValue("$poisoned", poisoned);
        command.Parameters.AddWithValue("$overflowed", overflowed);
        command.Parameters.AddWithValue("$expired", expired);
        command.ExecuteNonQuery();
    }
}
