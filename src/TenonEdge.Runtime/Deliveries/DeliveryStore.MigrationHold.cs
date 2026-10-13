using System.Globalization;
using Microsoft.Data.Sqlite;

namespace TenonEdge.Runtime.Deliveries;

internal sealed partial class DeliveryStore
{
    internal DeliveryMigrationHoldReceipt? ReadMigrationHold(Guid intentId)
    {
        EnsureInitialized();
        using var connection = Open(_readConnectionString);
        using var command = connection.CreateCommand();
        return ReadMigrationHold(command, _options.ConfigStoreId, intentId);
    }

    private static DeliveryMigrationHoldReceipt? ReadMigrationHold(SqliteCommand command, string storeId, Guid intentId)
    {
        command.Parameters.Clear();
        command.Parameters.AddWithValue("$store", storeId);
        command.Parameters.AddWithValue("$intent", intentId.ToString("D"));
        command.CommandText = """
            SELECT request_hash, connection_id, old_fingerprint, held_records, created_at
            FROM delivery_migration_hold WHERE config_store_id = $store AND intent_id = $intent;
            """;
        using var reader = command.ExecuteReader();
        return reader.Read() ? new(storeId, intentId, reader.GetString(0), reader.GetInt64(1), reader.GetString(2),
            reader.GetInt32(3), DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4))) : null;
    }

    // 调用方已持有跨 COMMIT 屏障或尚未启动任何发送者。隔离是提交后安全动作，不按待发配额拒绝；
    // 即使失败区暂时转为全局隔离占用超限，也不能让原记录继续发送。真实存储失败仍整体回滚并保留屏障。
    internal DeliveryWriteResult HoldMigration(SqliteConnection connection, SqliteTransaction transaction, DeliveryMigrationHold hold, long now)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT config_store_id FROM queue_meta WHERE id = 1";
        if (hold.ConfigStoreId != _options.ConfigStoreId || !Equals(command.ExecuteScalar(), hold.ConfigStoreId))
        { return DeliveryWriteResult.Rejected(DeliveryMigrationErrors.StoreChanged); }
        var previous = ReadMigrationHold(command, hold.ConfigStoreId, hold.IntentId);
        if (previous is not null)
        {
            return previous.RequestHash == hold.RequestHash
                ? new(DeliveryWriteStatus.Committed, [], previous.HeldRecords, null) { MigrationHold = previous }
                : DeliveryWriteResult.Rejected(DeliveryMigrationErrors.OperationChanged);
        }
        command.Parameters.Clear();
        command.Parameters.AddWithValue("$connection", hold.ConnectionId);
        command.Parameters.AddWithValue("$old", hold.OldFingerprint);
        command.CommandText = """
            CREATE TEMP TABLE IF NOT EXISTS migration_hold_selection (seq INTEGER PRIMARY KEY, previous_state TEXT NOT NULL);
            DELETE FROM migration_hold_selection;
            INSERT INTO migration_hold_selection
            SELECT seq, state FROM delivery WHERE connection_id = $connection AND fingerprint = $old AND migration_intent_id IS NULL
                AND (state IN ('pending', 'failed') OR (state = 'quarantined' AND quarantine_reason = 'targetChanged'));
            SELECT COUNT(*) FROM migration_hold_selection;
            """;
        var count = Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        QuarantineWhere(connection, transaction, "seq IN (SELECT seq FROM migration_hold_selection)", DeliveryQuarantineReasons.TargetChanged);
        command.Parameters.AddWithValue("$intent", hold.IntentId.ToString("D"));
        command.CommandText = """
            UPDATE delivery SET migration_intent_id = $intent,
                migration_previous_state = (SELECT previous_state FROM migration_hold_selection s WHERE s.seq = delivery.seq)
            WHERE seq IN (SELECT seq FROM migration_hold_selection);
            """;
        if (command.ExecuteNonQuery() != count)
        { throw new DeliveryStoreException("等待迁移的隔离范围未完整更新。"); }
        command.Parameters.AddWithValue("$store", hold.ConfigStoreId);
        command.Parameters.AddWithValue("$hash", hold.RequestHash);
        command.Parameters.AddWithValue("$count", count);
        command.Parameters.AddWithValue("$now", now);
        command.CommandText = """
            INSERT INTO delivery_migration_hold (config_store_id, intent_id, request_hash, connection_id, old_fingerprint, held_records, created_at)
            VALUES ($store, $intent, $hash, $connection, $old, $count, $now);
            """;
        command.ExecuteNonQuery();
        var receipt = new DeliveryMigrationHoldReceipt(hold.ConfigStoreId, hold.IntentId, hold.RequestHash,
            hold.ConnectionId, hold.OldFingerprint, count, DateTimeOffset.FromUnixTimeMilliseconds(now));
        return new(DeliveryWriteStatus.Committed, [], count, null) { MigrationHold = receipt };
    }
}
