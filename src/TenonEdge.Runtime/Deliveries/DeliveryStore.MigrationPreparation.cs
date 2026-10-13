using System.Globalization;
using Microsoft.Data.Sqlite;

namespace TenonEdge.Runtime.Deliveries;

internal sealed partial class DeliveryStore
{
    internal IReadOnlyList<DeliveryMigrationCandidate> ReadMigrationCandidates(long connectionId, IReadOnlyList<DeliveryMigrationKey> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (connectionId is < 1 or > 9007199254740991L || keys.Count is < 1 or > 1000 ||
            keys.Any(key => key is null || key.Sequence < 1 || key.DeliveryId == Guid.Empty || key.MigrationId is < 1) ||
            keys.Select(key => key.Sequence).Distinct().Count() != keys.Count || keys.Select(key => key.DeliveryId).Distinct().Count() != keys.Count)
        { throw new ArgumentException("请选择同一源连接的1至1000条不同记录。"); }
        EnsureInitialized();
        using var connection = Open(_readConnectionString);
        using var transaction = connection.BeginTransaction(deferred: true);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // 仅拼接已经校验的整数；不接受SQL片段、状态名称或客户端提供的指纹/摘要。
        var sequences = string.Join(',', keys.Select(key => key.Sequence.ToString(CultureInfo.InvariantCulture)));
        var predicate = $"connection_id = $source AND seq IN ({sequences})";
        command.Parameters.AddWithValue("$source", connectionId);
        command.CommandText = $"""
            SELECT COUNT(*), COALESCE(SUM(length(CAST(fingerprint_summary AS BLOB)) + length(CAST(fingerprint AS BLOB))), 0),
                COALESCE(MAX(length(CAST(fingerprint_summary AS BLOB))), 0), COALESCE(MAX(length(CAST(fingerprint AS BLOB))), 0)
            FROM delivery WHERE {predicate};
            """;
        using (var reader = command.ExecuteReader())
        {
            reader.Read();
            if (reader.GetInt32(0) != keys.Count)
            { throw new DeliveryMigrationSelectionException(DeliveryMigrationErrors.SelectionChanged); }
            if (reader.GetInt64(1) > 4 * 1024 * 1024 || reader.GetInt64(2) > TenonEdge.Abstractions.Contracts.OutputRecipientIdentity.MaximumSummaryBytes || reader.GetInt64(3) > 1024)
            { throw new DeliveryMigrationSelectionException("delivery.migrationIdentityBudget"); }
        }
        var expected = keys.ToDictionary(key => key.Sequence);
        command.CommandText = $"SELECT seq, delivery_id, fingerprint, fingerprint_summary, state, created_at, migration_id FROM delivery WHERE {predicate} ORDER BY seq";
        var result = new List<DeliveryMigrationCandidate>(keys.Count);
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var sequence = reader.GetInt64(0);
                var id = Guid.Parse(reader.GetString(1));
                var migration = reader.IsDBNull(6) ? (long?)null : reader.GetInt64(6);
                if (expected[sequence].DeliveryId != id || expected[sequence].MigrationId != migration)
                { throw new DeliveryMigrationSelectionException(DeliveryMigrationErrors.SelectionChanged); }
                var state = reader.GetString(4) switch
                {
                    "pending" => DeliveryState.Pending,
                    "failed" => DeliveryState.Failed,
                    "quarantined" => DeliveryState.Quarantined,
                    _ => throw new DeliveryStoreException("队列区域无效。"),
                };
                result.Add(new(new(sequence, id, reader.GetString(2), migration), reader.GetString(3), state,
                    DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(5))));
            }
        }
        transaction.Commit();
        return result;
    }
}
