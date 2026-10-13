using System.Globalization;
using Microsoft.Data.Sqlite;

namespace TenonEdge.Runtime.Deliveries;

internal sealed partial class DeliveryStore
{
    internal static DeliveryWriteResult ClearPending(SqliteConnection connection, SqliteTransaction transaction, long connectionId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM delivery WHERE connection_id = $target AND state = 'pending'";
        command.Parameters.AddWithValue("$target", connectionId);
        var affected = command.ExecuteNonQuery();
        AddCounter(connection, transaction, "manuallyClearedPendingRecords", affected);
        AddCounter(connection, transaction, $"target/{connectionId.ToString(CultureInfo.InvariantCulture)}/manuallyClearedPendingRecords", affected);
        return new(DeliveryWriteStatus.Committed, [], affected, null);
    }
}

internal sealed class ClearPendingRequest(long connectionId) : WriteRequest
{
    public long ConnectionId { get; } = connectionId;
    public override int RecordCount => 1; // 单个原子管理请求，不把目标记录分批删除。
}
