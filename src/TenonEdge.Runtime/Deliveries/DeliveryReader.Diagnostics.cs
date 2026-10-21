using Microsoft.Data.Sqlite;

namespace TenonEdge.Runtime.Deliveries;

internal sealed partial class DeliveryReader
{
    internal IReadOnlyDictionary<long, long> ReadPendingCounts(IReadOnlyCollection<long> connectionIds)
    {
        ArgumentNullException.ThrowIfNull(connectionIds);
        if (connectionIds.Count > 1000 || connectionIds.Any(id => id <= 0))
        { throw new ArgumentException("连接计数查询范围无效。", nameof(connectionIds)); }
        using var connection = openConnection();
        if (connectionIds.Count == 0)
        { return new Dictionary<long, long>(); }
        using var command = connection.CreateCommand();
        var parameters = new List<string>();
        foreach (var id in connectionIds.Distinct())
        {
            var name = "$id" + parameters.Count;
            parameters.Add("(" + name + ")");
            command.Parameters.AddWithValue(name, id);
        }
        command.CommandText = $"""
            WITH ids(connection_id) AS (VALUES {string.Join(",", parameters)})
            SELECT ids.connection_id, COUNT(d.seq) FROM ids
            LEFT JOIN delivery d ON d.connection_id = ids.connection_id AND d.state = 'pending'
            GROUP BY ids.connection_id;
            """;
        using var reader = command.ExecuteReader();
        var result = new Dictionary<long, long>();
        while (reader.Read())
        { result.Add(reader.GetInt64(0), reader.GetInt64(1)); }
        return result;
    }

    private const string UsageColumns = """
        COUNT(*) FILTER (WHERE d.state = 'pending'), COALESCE(SUM(d.byte_size) FILTER (WHERE d.state = 'pending'), 0),
        COUNT(*) FILTER (WHERE d.state = 'failed'), COALESCE(SUM(d.byte_size) FILTER (WHERE d.state = 'failed'), 0),
        COUNT(*) FILTER (WHERE d.state = 'quarantined'), COALESCE(SUM(d.byte_size) FILTER (WHERE d.state = 'quarantined'), 0),
        COUNT(*) FILTER (WHERE d.clock_anomaly = 1),
        COUNT(*) FILTER (WHERE d.state = 'quarantined' AND d.quarantine_reason = 'targetChanged'),
        COUNT(*) FILTER (WHERE d.state = 'quarantined' AND d.quarantine_reason = 'targetMissing'),
        COUNT(*) FILTER (WHERE d.state = 'quarantined' AND d.quarantine_reason = 'configStoreChanged'),
        COUNT(*) FILTER (WHERE d.state = 'quarantined' AND (d.quarantine_reason IS NULL OR d.quarantine_reason NOT IN ('targetChanged', 'targetMissing', 'configStoreChanged')))
        """;

    /// <summary>一个短只读事务得到全局占用与一页目标；始终不读取正文、请求头或身份指纹。
    /// 当前计划的零队列目标也可列出；已删除目标的待发/失败/隔离和持久策略计数不因此消失。</summary>
    internal DeliveryQueueReport ReadQueueReport(IReadOnlyCollection<long> activeConnections, long after, int limit)
    {
        ArgumentNullException.ThrowIfNull(activeConnections);
        if (activeConnections.Count > 500 || activeConnections.Any(id => id <= 0) || after < 0 || limit is < 1 or > 100)
        { throw new ArgumentException("队列统计目标或分页参数无效。"); }
        using var connection = openConnection();
        var captured = time.GetUtcNow();
        using var transaction = connection.BeginTransaction(deferred: true);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT " + UsageColumns + " FROM delivery d";
        DeliveryQueueUsage usage;
        using (var reader = command.ExecuteReader())
        { reader.Read(); usage = ReadUsage(reader, 0); }
        command.CommandText = """
            SELECT COALESCE(SUM(quota_rejected), 0), COALESCE(SUM(expired), 0), COALESCE(SUM(quarantined), 0)
            FROM delivery_queue_totals;
            """;
        long quota;
        long expired;
        long quarantined;
        using (var reader = command.ExecuteReader())
        { reader.Read(); quota = reader.GetInt64(0); expired = reader.GetInt64(1); quarantined = reader.GetInt64(2); }
        command.CommandText = """
            SELECT COALESCE(SUM(rejected), 0), COALESCE(SUM(poisoned), 0), COALESCE(SUM(overflowed), 0), COALESCE(SUM(expired), 0)
            FROM delivery_failure_totals;
            """;
        DeliveryQueuePolicyCounters counters;
        using (var reader = command.ExecuteReader())
        { reader.Read(); counters = new(quota, expired, quarantined, reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3)); }
        var active = new List<string>();
        foreach (var id in activeConnections.Distinct())
        {
            var name = "$active" + active.Count;
            command.Parameters.AddWithValue(name, id);
            active.Add("(" + name + ")");
        }
        var activeSql = active.Count == 0 ? "SELECT NULL WHERE 0" : "VALUES " + string.Join(", ", active);
        command.CommandText = $$"""
            WITH active(connection_id) AS ({{activeSql}}), ids AS (
                SELECT connection_id FROM delivery UNION SELECT connection_id FROM delivery_queue_totals
                UNION SELECT connection_id FROM delivery_failure_totals UNION SELECT connection_id FROM active
            ), page AS (SELECT connection_id FROM ids WHERE connection_id > $after ORDER BY connection_id LIMIT $limit)
            SELECT p.connection_id, {{UsageColumns}},
                MAX(COALESCE(q.quota_rejected, 0)), MAX(COALESCE(q.expired, 0)), MAX(COALESCE(q.quarantined, 0)),
                MAX(COALESCE(f.rejected, 0)), MAX(COALESCE(f.poisoned, 0)), MAX(COALESCE(f.overflowed, 0)), MAX(COALESCE(f.expired, 0)),
                head.seq, head.created_at, head.clock_anomaly
            FROM page p LEFT JOIN delivery d ON d.connection_id = p.connection_id
                LEFT JOIN delivery_queue_totals q ON q.connection_id = p.connection_id
                LEFT JOIN delivery_failure_totals f ON f.connection_id = p.connection_id
                LEFT JOIN delivery head ON head.seq = (SELECT MIN(seq) FROM delivery WHERE connection_id = p.connection_id AND state = 'pending')
            GROUP BY p.connection_id ORDER BY p.connection_id;
            """;
        command.Parameters.AddWithValue("$after", after);
        command.Parameters.AddWithValue("$limit", limit + 1);
        var targets = new List<DeliveryTargetQueueReport>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                targets.Add(new(reader.GetInt64(0), ReadUsage(reader, 1),
                    new(reader.GetInt64(12), reader.GetInt64(13), reader.GetInt64(14), reader.GetInt64(15),
                        reader.GetInt64(16), reader.GetInt64(17), reader.GetInt64(18)),
                    reader.IsDBNull(19) ? null : reader.GetInt64(19),
                    reader.IsDBNull(20) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(20)),
                    !reader.IsDBNull(21) && reader.GetBoolean(21)));
            }
        }
        transaction.Commit();
        // 文件长度不是逻辑配额，也不是经过目标硬件校准的 SQLite 放大系数。仅报告当前真实大小。
        return new(captured, usage, counters, targets.Take(limit).ToArray(), targets.Count > limit ? targets[limit - 1].ConnectionId : null, ReadQueueFiles());
    }

    private static DeliveryQueueUsage ReadUsage(SqliteDataReader reader, int start) => new(
        reader.GetInt64(start), reader.GetInt64(start + 1), reader.GetInt64(start + 2), reader.GetInt64(start + 3),
        reader.GetInt64(start + 4), reader.GetInt64(start + 5), reader.GetInt64(start + 6), reader.GetInt64(start + 7),
        reader.GetInt64(start + 8), reader.GetInt64(start + 9), reader.GetInt64(start + 10));

    private DeliveryQueueFiles? ReadQueueFiles()
    {
        try
        {
            return new(new FileInfo(databasePath).Length, LengthIfPresent(databasePath + "-wal"), LengthIfPresent(databasePath + "-shm"));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { return null; }
        static long LengthIfPresent(string path)
        {
            try
            { return new FileInfo(path).Length; }
            catch (FileNotFoundException) { return 0; }
        }
    }

    internal IReadOnlyDictionary<string, long> ReadCounters()
    {
        using var connection = openConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name, value FROM queue_counters ORDER BY name";
        using var reader = command.ExecuteReader();
        var counters = new Dictionary<string, long>(StringComparer.Ordinal);
        while (reader.Read())
        {
            counters.Add(reader.GetString(0), reader.GetInt64(1));
        }

        return counters;
    }
}
