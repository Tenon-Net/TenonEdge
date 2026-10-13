using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TenonEdge.Abstractions.Contracts;

namespace TenonEdge.Runtime.Deliveries;

internal sealed partial class DeliveryStore
{
    internal DeliveryInspectionPage Inspect(DeliveryInspectionQuery query, bool allowPending = false)
    {
        ArgumentNullException.ThrowIfNull(query);
        query.Validate(allowPending);
        EnsureInitialized();
        var captured = _time.GetUtcNow();
        using var connection = Open(_readConnectionString);
        using var transaction = connection.BeginTransaction(deferred: true);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // 刻意不复用 SelectColumns/Read：Viewer 查询不能先加载正文再在 DTO 中丢掉。
        var conditions = InspectionConditions(command, query);
        command.CommandText = $"""
            SELECT {InspectionColumns}
            FROM delivery WHERE {string.Join(" AND ", conditions)} ORDER BY seq LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", query.Limit + 1);
        var items = new List<DeliveryInspectionItem>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                items.Add(ReadInspectionItem(reader, query.State));
            }
        }
        transaction.Commit();
        return new(captured, items.Take(query.Limit).ToArray(), items.Count > query.Limit ? items[query.Limit - 1].Sequence : null);
    }

    private const string InspectionColumns = """
        seq, delivery_id, connection_id, device_key, route_id, node_label, revision,
        created_at, failed_at, failure_count, quarantine_reason, last_error, byte_size, clock_anomaly, migration_id
        """;

    // 查看和导出使用同一筛选口径；导出另外加确认水位，而不把分页上限作为总条数上限。
    private static List<string> InspectionConditions(SqliteCommand command, DeliveryInspectionQuery query)
    {
        var conditions = new List<string> { "state = $state", "seq > $after" };
        command.Parameters.AddWithValue("$state", query.State switch { DeliveryState.Pending => "pending", DeliveryState.Failed => "failed", _ => "quarantined" });
        command.Parameters.AddWithValue("$after", query.After);
        if (query.ConnectionId is { } id)
        { conditions.Add("connection_id = $connection"); command.Parameters.AddWithValue("$connection", id); }
        if (query.DeviceKey is { } device)
        { conditions.Add("device_key = $device"); command.Parameters.AddWithValue("$device", device); }
        if (query.Reason is { } reason)
        {
            conditions.Add("(instr(COALESCE(last_error, ''), $reason) > 0 OR instr(COALESCE(quarantine_reason, ''), $reason) > 0)");
            command.Parameters.AddWithValue("$reason", reason);
        }
        if (query.From is { } from)
        { conditions.Add("created_at >= $from"); command.Parameters.AddWithValue("$from", CeilingMilliseconds(from)); }
        if (query.To is { } to)
        { conditions.Add("created_at < $to"); command.Parameters.AddWithValue("$to", CeilingMilliseconds(to)); }
        return conditions;
    }

    private static DeliveryInspectionItem ReadInspectionItem(SqliteDataReader reader, DeliveryState state) => new(
        reader.GetInt64(0), Guid.Parse(reader.GetString(1)), reader.GetInt64(2), reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetInt64(4), reader.IsDBNull(5) ? null : reader.GetString(5), reader.GetInt64(6),
        DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(7)),
        reader.IsDBNull(8) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(8)), reader.GetInt32(9), state,
        reader.IsDBNull(10) ? null : reader.GetString(10), reader.IsDBNull(11) ? null : reader.GetString(11),
        reader.GetInt64(12), reader.GetBoolean(13), reader.IsDBNull(14) ? null : reader.GetInt64(14));

    // 存储精度为毫秒；对两个比较边界均向上取整，保留 API 的左闭右开语义，不把半毫秒条件向下截断。
    private static long CeilingMilliseconds(DateTimeOffset value) => value.ToUnixTimeMilliseconds() +
        (value.Ticks % TimeSpan.TicksPerMillisecond == 0 ? 0 : 1);

    internal DeliveryBodyPreview? InspectBody(Guid deliveryId, DeliveryState state)
    {
        new DeliveryInspectionQuery(state).Validate();
        if (deliveryId == Guid.Empty)
        { throw new ArgumentException("投递编号不能为空。", nameof(deliveryId)); }
        EnsureInitialized();
        using var connection = Open(_readConnectionString);
        using var command = connection.CreateCommand();
        // SQL 层截取 BLOB，不把整条报文读到托管内存后再截断。长度与前缀来自同一查询快照。
        command.CommandText = "SELECT length(payload), substr(payload, 1, 65536), length(CAST(response_summary AS BLOB)), substr(CAST(response_summary AS BLOB), 1, 8192) FROM delivery WHERE delivery_id = $id AND state = $state";
        command.Parameters.AddWithValue("$id", deliveryId.ToString("D"));
        command.Parameters.AddWithValue("$state", state == DeliveryState.Failed ? "failed" : "quarantined");
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        { return null; }
        HttpResponseSummary? summary = null;
        if (!reader.IsDBNull(2))
        {
            if (reader.GetInt64(2) > HttpResponseSummary.MaximumSerializedBytes)
            { throw new DeliveryStoreException("响应摘要超过读取上限。"); }
            try
            { summary = JsonSerializer.Deserialize<HttpResponseSummary>((byte[])reader.GetValue(3)) ?? throw new JsonException(); }
            catch (Exception error) when (error is JsonException or ArgumentException)
            { throw new DeliveryStoreException("响应摘要不可读取。"); }
        }
        var total = reader.GetInt64(0);
        var prefix = (byte[])reader.GetValue(1);
        try
        {
            var utf8 = new UTF8Encoding(false, true);
            var chars = new char[prefix.Length];
            utf8.GetDecoder().Convert(prefix, chars, flush: total == prefix.Length, out _, out var charsUsed, out _);
            var content = new string(chars, 0, charsUsed);
            var displayed = utf8.GetByteCount(content);
            return new(deliveryId, state, "utf-8", content, total, displayed, displayed < total, summary);
        }
        catch (DecoderFallbackException)
        {
            return new(deliveryId, state, "base64", Convert.ToBase64String(prefix), total, prefix.Length, prefix.Length < total, summary);
        }
    }
}
