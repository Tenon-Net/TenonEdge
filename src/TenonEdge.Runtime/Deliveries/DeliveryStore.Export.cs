using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace TenonEdge.Runtime.Deliveries;

internal sealed partial class DeliveryStore
{
    private const int ExportPageSize = 16;
    private static readonly UTF8Encoding ExportUtf8 = new(false, true);

    internal DeliveryExportReview ReviewExport(DeliveryExportFilter filter, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(filter);
        filter.Query.Validate();
        token.ThrowIfCancellationRequested();
        EnsureInitialized();
        var captured = _time.GetUtcNow();
        using var connection = Open(_readConnectionString);
        using var transaction = connection.BeginTransaction(deferred: true);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COALESCE(MAX(seq), 0) FROM delivery";
        var through = (long)command.ExecuteScalar()!;
        SetExportQuery(command, filter.Query, through, includePayload: false);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new ArrayBufferWriter<byte>();
        long count = 0, payloadBytes = 0, maximumFileBytes = 0;
        DateTimeOffset? first = null, last = null;
        // 单次本地元数据扫描，不读取正文、协议头、身份或凭证；没有逐条清单常驻内存。
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                var item = ReadInspectionItem(reader, filter.State);
                var length = ReadExportPayloadLength(reader);
                AppendExportMetadata(hash, buffer, item, length);
                count++;
                payloadBytes = checked(payloadBytes + length);
                // UTF-8 有效时每个原始字节最多写成六字节 JSON 转义；Base64 小于此上界。
                // 128 覆盖 encoding/content 字段、引号与换行，不把 ByteSize 当正文长度。
                maximumFileBytes = checked(maximumFileBytes + buffer.WrittenCount + 128 + 6 * length);
                first = first is null || item.CreatedAt < first ? item.CreatedAt : first;
                last = last is null || item.CreatedAt > last ? item.CreatedAt : last;
            }
        }
        transaction.Commit();
        return new(this, filter, captured, through, count, payloadBytes, maximumFileBytes, first, last,
            Convert.ToHexString(hash.GetHashAndReset()));
    }

    internal async Task<bool> WriteExportSpoolAsync(DeliveryExportReview review, Stream spool, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(review);
        ArgumentNullException.ThrowIfNull(spool);
        if (!ReferenceEquals(review.Owner, this))
        { throw new ArgumentException("导出核对凭据不属于此缓存实例。", nameof(review)); }
        if (!spool.CanWrite || !spool.CanSeek || spool.Position != 0 || spool.Length != 0)
        { throw new ArgumentException("导出必须先写入空的私有可定位暂存文件，不能直接写 HTTP 响应。", nameof(spool)); }
        token.ThrowIfCancellationRequested();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new ArrayBufferWriter<byte>();
        long after = 0, count = 0, payloadBytes = 0;
        while (after < review.ThroughSequence)
        {
            token.ThrowIfCancellationRequested();
            // 每页最多 16 × 256 KiB 正文；读连接与事务在任何文件/网络等待之前释放。
            var page = ReadExportPage(review.Filter, after, review.ThroughSequence, token);
            if (page.Count == 0)
            { break; }
            foreach (var (item, payload) in page)
            {
                token.ThrowIfCancellationRequested();
                count++;
                payloadBytes = checked(payloadBytes + payload.Length);
                if (count > review.Count || payloadBytes > review.PayloadBytes)
                { return false; }
                AppendExportMetadata(hash, buffer, item, payload.Length);
                buffer.Clear();
                using (var writer = new Utf8JsonWriter(buffer))
                {
                    WriteExportMetadata(writer, item, payload.Length);
                    string encoding, content;
                    try
                    { content = ExportUtf8.GetString(payload); encoding = "utf-8"; }
                    catch (DecoderFallbackException) { content = Convert.ToBase64String(payload); encoding = "base64"; }
                    writer.WriteString("encoding", encoding);
                    writer.WriteString("content", content);
                    writer.WriteEndObject();
                }
                buffer.GetSpan(1)[0] = (byte)'\n';
                buffer.Advance(1);
                if (spool.Position > review.MaximumFileBytes - buffer.WrittenCount)
                { return false; }
                await spool.WriteAsync(buffer.WrittenMemory, token);
                after = item.Sequence;
            }
        }
        token.ThrowIfCancellationRequested();
        // 页间允许并发写入。只有有序身份、迁移代次及全部导出元数据完全匹配，暂存文件才可发布。
        // 已读出的冻结记录随后被清理不影响文件；未读出的记录变化则导致不匹配。
        return count == review.Count && payloadBytes == review.PayloadBytes &&
            Convert.ToHexString(hash.GetHashAndReset()) == review.SelectionHash;
    }

    private List<(DeliveryInspectionItem Item, byte[] Payload)> ReadExportPage(DeliveryExportFilter filter,
        long after, long through, CancellationToken token)
    {
        using var connection = Open(_readConnectionString);
        using var transaction = connection.BeginTransaction(deferred: true);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        SetExportQuery(command, filter.Query with { After = after }, through, includePayload: true);
        var page = new List<(DeliveryInspectionItem, byte[])>(ExportPageSize);
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                ReadExportPayloadLength(reader); // 损坏的超大正文在托管内存分配之前显式失败。
                page.Add((ReadInspectionItem(reader, filter.State), (byte[])reader.GetValue(16)));
            }
        }
        transaction.Commit();
        return page;
    }

    private static void SetExportQuery(SqliteCommand command, DeliveryInspectionQuery query, long through, bool includePayload)
    {
        var conditions = InspectionConditions(command, query);
        conditions.Add("seq <= $through");
        command.Parameters.AddWithValue("$through", through);
        // 以整数主键按水位扫描，避免 state/created 索引在每页重新扫描并排序全部匹配记录。
        command.CommandText = $"""
            SELECT {InspectionColumns}, length(payload){(includePayload ? ", payload" : "")}
            FROM delivery NOT INDEXED WHERE {string.Join(" AND ", conditions)} ORDER BY seq
            {(includePayload ? "LIMIT $limit" : "")};
            """;
        if (includePayload)
        { command.Parameters.AddWithValue("$limit", ExportPageSize); }
    }

    private static long ReadExportPayloadLength(SqliteDataReader reader)
    {
        var length = reader.GetInt64(15);
        if (length is < 1 or > DeliveryDraft.MaximumPayloadBytes)
        { throw new DeliveryStoreException("导出记录的正文长度不符合队列约束，不能输出部分文件。"); }
        return length;
    }

    private static void AppendExportMetadata(IncrementalHash hash, ArrayBufferWriter<byte> buffer,
        DeliveryInspectionItem item, long length)
    {
        buffer.Clear();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteExportMetadata(writer, item, length);
            writer.WriteEndObject();
        }
        hash.AppendData(buffer.WrittenSpan);
        hash.AppendData("\n"u8);
    }

    // 明确白名单。正文作为字符串保留原文，不解析/重排其中的 JSON 数字；大整数标识用十进制字符串。
    private static void WriteExportMetadata(Utf8JsonWriter writer, DeliveryInspectionItem item, long length)
    {
        writer.WriteStartObject();
        writer.WriteNumber("formatVersion", 1);
        writer.WriteString("sequence", item.Sequence.ToString(CultureInfo.InvariantCulture));
        writer.WriteString("deliveryId", item.DeliveryId);
        writer.WriteString("connectionId", item.ConnectionId.ToString(CultureInfo.InvariantCulture));
        writer.WriteString("deviceKey", item.DeviceKey);
        writer.WriteString("routeId", item.RouteId?.ToString(CultureInfo.InvariantCulture));
        writer.WriteString("nodeLabel", item.NodeLabel);
        writer.WriteString("revision", item.Revision.ToString(CultureInfo.InvariantCulture));
        writer.WriteString("state", item.State == DeliveryState.Failed ? "failed" : "quarantined");
        writer.WriteString("createdAt", item.CreatedAt);
        if (item.FailedAt is { } failedAt)
        { writer.WriteString("failedAt", failedAt); }
        else
        { writer.WriteNull("failedAt"); }
        writer.WriteNumber("failureCount", item.FailureCount);
        writer.WriteString("lastError", item.LastError);
        writer.WriteString("quarantineReason", item.QuarantineReason);
        writer.WriteString("migrationId", item.MigrationId?.ToString(CultureInfo.InvariantCulture));
        writer.WriteBoolean("clockAnomaly", item.ClockAnomaly);
        writer.WriteNumber("payloadBytes", length);
    }
}
