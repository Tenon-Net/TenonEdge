using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

namespace TenonEdge.Runtime.Deliveries;

internal sealed partial class DeliveryStore
{
    internal DeliveryMigrationReceipt? ReadMigrationReceipt(Guid operationId)
    {
        EnsureInitialized();
        using var connection = Open(_readConnectionString);
        using var command = connection.CreateCommand();
        return ReadMigrationReceipt(command, _options.ConfigStoreId, operationId);
    }

    private static DeliveryMigrationReceipt? ReadMigrationReceipt(SqliteCommand command, string configStoreId, Guid operationId)
    {
        command.Parameters.Clear();
        command.Parameters.AddWithValue("$store", configStoreId);
        command.Parameters.AddWithValue("$operation", operationId.ToString("D"));
        command.CommandText = """
            SELECT migration_id, request_hash, source_connection_id, target_connection_id, affected_records, completed_at
            FROM delivery_migration_receipt WHERE config_store_id = $store AND operation_id = $operation;
            """;
        using var reader = command.ExecuteReader();
        return reader.Read() ? new(reader.GetInt64(0), operationId, configStoreId, reader.GetString(1), reader.GetInt64(2),
            reader.GetInt64(3), reader.GetInt32(4), DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(5))) : null;
    }

    /// <summary>唯一写者事务：先校验完整选择与配额，再原位更新及写收据；不读正文，不拆批，不刷新 FIFO/保留期。
    /// 准入和双发送者暂停由运行时保证；本方法不证明协议类型、目标有效性或所有者身份。</summary>
    internal DeliveryWriteResult MigrateDeliveries(SqliteConnection connection, SqliteTransaction transaction,
        DeliveryMigrationCommand request, long now, bool storageProtected)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT config_store_id FROM queue_meta WHERE id = 1";
        if (request.ConfigStoreId != _options.ConfigStoreId || !Equals(command.ExecuteScalar(), request.ConfigStoreId))
        { return DeliveryWriteResult.Rejected(DeliveryMigrationErrors.StoreChanged); }
        var previous = ReadMigrationReceipt(command, request.ConfigStoreId, request.OperationId);
        if (previous is not null)
        {
            return previous.RequestHash == request.RequestHash
                ? new(DeliveryWriteStatus.Committed, [], previous.AffectedRecords, null) { Migration = previous }
                : DeliveryWriteResult.Rejected(DeliveryMigrationErrors.OperationChanged);
        }
        // 管理操作同样会增长 WAL；保护期间保守拒绝新的迁移，但仍能回读已提交的结果。
        if (storageProtected)
        { return DeliveryWriteResult.Rejected(DeliveryMigrationErrors.StorageProtection); }
        if (!SelectMigration(command, request))
        { return DeliveryWriteResult.Rejected(DeliveryMigrationErrors.SelectionChanged); }

        command.Parameters.Clear();
        command.Parameters.AddWithValue("$target", request.TargetConnectionId);
        command.Parameters.AddWithValue("$identityBytes", (long)Encoding.UTF8.GetByteCount(request.Fingerprint) + Encoding.UTF8.GetByteCount(request.Summary));
        command.CommandText = """
            UPDATE migration_selection SET new_bytes = (
                SELECT byte_size - length(CAST(fingerprint AS BLOB)) - length(CAST(fingerprint_summary AS BLOB)) + $identityBytes
                FROM delivery WHERE seq = migration_selection.seq
            );
            SELECT COUNT(*), COALESCE(SUM(s.new_bytes), 0),
                COUNT(*) FILTER (WHERE d.connection_id = $target AND d.state = 'pending'),
                COALESCE(SUM(d.byte_size) FILTER (WHERE d.connection_id = $target AND d.state = 'pending'), 0),
                COALESCE(SUM(d.byte_size) FILTER (WHERE d.state <> 'failed'), 0),
                COALESCE(SUM(s.new_bytes) FILTER (WHERE d.state <> 'failed'), 0),
                COUNT(*) FILTER (WHERE d.state = 'failed'),
                COUNT(*) FILTER (WHERE d.connection_id = $target AND d.state = 'failed')
            FROM migration_selection s JOIN delivery d ON d.seq = s.seq;
            """;
        int selected;
        long selectedBytes, alreadyPending, alreadyPendingBytes, previousGlobalBytes, nextGlobalBytes, failed, alreadyFailed;
        using (var reader = command.ExecuteReader())
        {
            reader.Read();
            selected = reader.GetInt32(0);
            selectedBytes = reader.GetInt64(1);
            alreadyPending = reader.GetInt64(2);
            alreadyPendingBytes = reader.GetInt64(3);
            previousGlobalBytes = reader.GetInt64(4);
            nextGlobalBytes = reader.GetInt64(5);
            failed = reader.GetInt64(6);
            alreadyFailed = reader.GetInt64(7);
        }
        if (selected > 0)
        {
            command.CommandText = """
                SELECT COUNT(*) FILTER (WHERE connection_id = $target AND state = 'pending'),
                    COALESCE(SUM(byte_size) FILTER (WHERE connection_id = $target AND state = 'pending'), 0),
                    COALESCE(SUM(byte_size) FILTER (WHERE state IN ('pending', 'quarantined')), 0),
                    COUNT(*) FILTER (WHERE connection_id = $target AND state = 'failed') FROM delivery;
                """;
            DeliveryMigrationQuotaDeficit deficit;
            using (var reader = command.ExecuteReader())
            {
                reader.Read();
                // §8.3 明确要求待发剩余额度容纳全部选择（包括迁移后仍为 failed 的记录）。
                // 同连接原已占用的 pending 先扣除，不能重复收费；全局实际仍只计 pending/quarantined。
                deficit = new(
                    Math.Max(0, checked(reader.GetInt64(0) - alreadyPending + selected - _options.MaximumPendingPerTarget)),
                    Math.Max(0, checked(reader.GetInt64(1) - alreadyPendingBytes + selectedBytes - _options.MaximumPendingBytesPerTarget)),
                    Math.Max(0, checked(reader.GetInt64(2) - previousGlobalBytes + nextGlobalBytes - _options.MaximumQueueBytes)),
                    Math.Max(0, checked(reader.GetInt64(3) - alreadyFailed + failed - _options.MaximumFailedPerTarget)));
            }
            if (deficit.Exceeded)
            { return DeliveryWriteResult.Rejected(DeliveryMigrationErrors.QuotaExceeded) with { MigrationQuota = deficit }; }
        }

        command.Parameters.Clear();
        command.Parameters.AddWithValue("$store", request.ConfigStoreId);
        command.Parameters.AddWithValue("$operation", request.OperationId.ToString("D"));
        command.Parameters.AddWithValue("$hash", request.RequestHash);
        command.Parameters.AddWithValue("$source", request.SourceConnectionId);
        command.Parameters.AddWithValue("$target", request.TargetConnectionId);
        command.Parameters.AddWithValue("$count", selected);
        command.Parameters.AddWithValue("$now", now);
        command.CommandText = """
            INSERT INTO delivery_migration_receipt (config_store_id, operation_id, request_hash, source_connection_id,
                target_connection_id, affected_records, completed_at)
            VALUES ($store, $operation, $hash, $source, $target, $count, $now) RETURNING migration_id;
            """;
        var migrationId = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        command.Parameters.AddWithValue("$migration", migrationId);
        command.Parameters.AddWithValue("$fingerprint", request.Fingerprint);
        command.Parameters.AddWithValue("$summary", request.Summary);
        command.CommandText = """
            UPDATE delivery SET connection_id = $target, fingerprint = $fingerprint, fingerprint_summary = $summary,
                state = CASE WHEN state = 'failed' THEN 'failed' ELSE 'pending' END,
                failure_count = 0, migration_id = $migration, quarantine_reason = NULL,
                failed_at = CASE WHEN state = 'failed' THEN failed_at ELSE NULL END, last_retried_at = NULL,
                byte_size = (SELECT new_bytes FROM migration_selection WHERE seq = delivery.seq)
            WHERE seq IN (SELECT seq FROM migration_selection);
            """;
        if (command.ExecuteNonQuery() != selected)
        { throw new DeliveryStoreException("迁移未更新完整选择，事务必须回滚。"); }
        AddCounter(connection, transaction, "manuallyMigratedRecords", selected);
        var receipt = new DeliveryMigrationReceipt(migrationId, request.OperationId, request.ConfigStoreId, request.RequestHash,
            request.SourceConnectionId, request.TargetConnectionId, selected, DateTimeOffset.FromUnixTimeMilliseconds(now));
        return new(DeliveryWriteStatus.Committed, [], selected, null) { Migration = receipt };
    }

    private static bool SelectMigration(SqliteCommand command, DeliveryMigrationCommand request)
    {
        command.Parameters.Clear();
        command.CommandText = """
            CREATE TEMP TABLE IF NOT EXISTS migration_selection (seq INTEGER PRIMARY KEY, delivery_id TEXT NOT NULL UNIQUE,
                fingerprint TEXT NOT NULL, previous_migration_id INTEGER, new_bytes INTEGER NOT NULL DEFAULT 0);
            DELETE FROM migration_selection;
            """;
        command.ExecuteNonQuery();
        command.Parameters.AddWithValue("$source", request.SourceConnectionId);
        if (request.OldFingerprint is not null)
        {
            command.Parameters.AddWithValue("$old", request.OldFingerprint);
            command.CommandText = """
                INSERT INTO migration_selection (seq, delivery_id, fingerprint, previous_migration_id)
                SELECT seq, delivery_id, fingerprint, migration_id FROM delivery WHERE connection_id = $source AND fingerprint = $old
                    AND (state IN ('pending', 'failed') OR (state = 'quarantined' AND quarantine_reason = 'targetChanged'));
                """;
            command.ExecuteNonQuery();
            return true;
        }
        command.CommandText = "INSERT INTO migration_selection (seq, delivery_id, fingerprint, previous_migration_id) VALUES ($seq, $id, $fp, $migration)";
        command.Parameters.Add("$seq", SqliteType.Integer);
        command.Parameters.Add("$id", SqliteType.Text);
        command.Parameters.Add("$fp", SqliteType.Text);
        command.Parameters.Add("$migration", SqliteType.Integer);
        foreach (var item in request.Items!)
        {
            command.Parameters["$seq"].Value = item.Sequence;
            command.Parameters["$id"].Value = item.DeliveryId.ToString("D");
            command.Parameters["$fp"].Value = item.Fingerprint;
            command.Parameters["$migration"].Value = (object?)item.MigrationId ?? DBNull.Value;
            command.ExecuteNonQuery();
        }
        command.CommandText = """
            SELECT EXISTS (
                SELECT 1 FROM migration_selection s JOIN delivery d ON d.delivery_id = s.delivery_id
                WHERE d.seq <> s.seq OR d.connection_id <> $source OR d.fingerprint <> s.fingerprint OR d.migration_id IS NOT s.previous_migration_id
            ) OR EXISTS (
                SELECT 1 FROM migration_selection s JOIN delivery d ON d.seq = s.seq WHERE d.delivery_id <> s.delivery_id
            );
            """;
        if (Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 0)
        { return false; }
        // ACK/保留期清理后已不存在的记录不参与实际迁移；人工重试换 seq 或另一迁移换代不是“已删除”。
        command.CommandText = "DELETE FROM migration_selection WHERE NOT EXISTS (SELECT 1 FROM delivery d WHERE d.seq = migration_selection.seq)";
        command.ExecuteNonQuery();
        return true;
    }
}
