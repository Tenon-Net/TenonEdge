using System.Globalization;
using SqlSugar;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Host.Features.Audit;
using TenonEdge.Host.Storage;

namespace TenonEdge.Host.Features.Deliveries;

internal sealed partial class OutputControlStore(ConfigDatabase database, AuditTrail audit, TimeProvider time) : IOutputControl
{
    public OutputControlState Read(long connectionId)
    {
        using var client = database.CreateIsolatedClient();
        // 保留发送前实时检查，不以缓存替代管理员刚提交的暂停/恢复。
        using var reader = client.Ado.GetDataReader(
            "SELECT manually_paused, version FROM output_control WHERE connection_id = @id",
            new SugarParameter("@id", connectionId));
        return !reader.Read() ? new(false, null) : new(reader.GetBoolean(0), reader.GetString(1));
    }

    public IReadOnlyDictionary<long, OutputControlState> ReadMany(long[] connectionIds)
    {
        using var client = database.CreateIsolatedClient();
        return client.Queryable<OutputControlRecord>().Where(row => connectionIds.Contains(row.ConnectionId)).ToList()
            .ToDictionary(row => row.ConnectionId, row => new OutputControlState(row.ManuallyPaused, row.Version));
    }

    public OutputControlState Write(long connectionId, OutputControlRequest request, AuditAccount account, string? sourceIp)
    {
        if (connectionId is < 1 or > 9007199254740991L || !request.Confirmed)
        {
            throw new ArgumentException("连接 ID 须为安全整数，操作须显式确认。");
        }

        return database.InTransaction(client =>
        {
            var row = client.Queryable<OutputControlRecord>().InSingle(connectionId);
            if (row?.Version != request.ExpectedVersion)
            {
                throw new OutputControlConflictException();
            }

            var existed = row is not null;
            row ??= new OutputControlRecord { ConnectionId = connectionId };
            row.ManuallyPaused = request.Paused;
            row.Version = Guid.NewGuid().ToString("N");
            row.UpdatedAt = time.GetUtcNow().ToUnixTimeMilliseconds();
            if (existed)
            {
                client.Updateable(row).ExecuteCommand();
            }
            else
            {
                client.Insertable(row).ExecuteCommand();
            }

            audit.Append(client, new AuditEntry(request.Paused ? "delivery.pause" : "delivery.resume", AuditResults.Success,
                account, sourceIp, connectionId.ToString(CultureInfo.InvariantCulture), "管理员显式确认目标发送状态变更。"));
            return new OutputControlState(row.ManuallyPaused, row.Version);
        });
    }
}

[SugarTable("output_control")]
internal sealed class OutputControlRecord
{
    [SugarColumn(ColumnName = "connection_id", IsPrimaryKey = true)]
    public long ConnectionId { get; set; }
    [SugarColumn(ColumnName = "manually_paused")]
    public bool ManuallyPaused { get; set; }
    [SugarColumn(ColumnName = "version")]
    public string Version { get; set; } = string.Empty;
    [SugarColumn(ColumnName = "updated_at")]
    public long UpdatedAt { get; set; }
}

internal sealed record OutputControlRequest(bool Paused, bool Confirmed, string? ExpectedVersion = null);
internal sealed class OutputControlConflictException : Exception;
