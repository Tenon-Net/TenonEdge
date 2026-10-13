using System.Globalization;
using Microsoft.Data.Sqlite;

namespace TenonEdge.Runtime.Deliveries;

internal sealed partial class DeliveryStore
{
    /// <summary>由唯一写者在已有事务中检查完整选择再删除；不加载正文，不影响其他区域或确认后新增的记录。</summary>
    internal static DeliveryWriteResult Cleanup(SqliteConnection connection, SqliteTransaction transaction, DeliveryCleanupSelection selection)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TEMP TABLE IF NOT EXISTS cleanup_selection (seq INTEGER PRIMARY KEY, delivery_id TEXT NOT NULL UNIQUE, connection_id INTEGER NOT NULL, migration_id INTEGER NULL);
            DELETE FROM cleanup_selection;
            """;
        command.ExecuteNonQuery();
        command.CommandText = "INSERT INTO cleanup_selection VALUES ($seq, $id, $connection, $migration)";
        command.Parameters.Add("$seq", SqliteType.Integer);
        command.Parameters.Add("$id", SqliteType.Text);
        command.Parameters.Add("$connection", SqliteType.Integer);
        command.Parameters.Add("$migration", SqliteType.Integer);
        foreach (var item in selection.Items)
        {
            command.Parameters["$seq"].Value = item.Sequence;
            command.Parameters["$id"].Value = item.DeliveryId.ToString("D");
            command.Parameters["$connection"].Value = item.ConnectionId;
            command.Parameters["$migration"].Value = (object?)item.MigrationId ?? DBNull.Value;
            command.ExecuteNonQuery();
        }
        command.Parameters.Clear();
        command.Parameters.AddWithValue("$state", selection.State == DeliveryState.Failed ? "failed" : "quarantined");
        command.CommandText = """
            SELECT COUNT(*) FROM delivery d JOIN cleanup_selection s
                ON d.seq = s.seq AND d.delivery_id = s.delivery_id AND d.connection_id = s.connection_id AND d.migration_id IS s.migration_id
            WHERE d.state = $state;
            """;
        if (Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) != selection.Items.Count)
        { return DeliveryWriteResult.Rejected(DeliveryCleanupErrors.SelectionChanged); }
        command.CommandText = "DELETE FROM delivery WHERE seq IN (SELECT seq FROM cleanup_selection) AND state = $state";
        var affected = command.ExecuteNonQuery();
        if (affected != selection.Items.Count)
        { throw new DeliveryStoreException("未清理完整选择，事务必须回滚。"); }
        // 人工丢弃与发送确认分开；计数与删除同一事务，重放旧选择不会重复计数。
        var counter = selection.State == DeliveryState.Failed ? "manuallyClearedFailedRecords" : "manuallyClearedQuarantinedRecords";
        AddCounter(connection, transaction, counter, affected);
        foreach (var group in selection.Items.GroupBy(item => item.ConnectionId))
        { AddCounter(connection, transaction, $"target/{group.Key.ToString(CultureInfo.InvariantCulture)}/{counter}", group.Count()); }
        return new(DeliveryWriteStatus.Committed, [], affected, null);
    }
}
