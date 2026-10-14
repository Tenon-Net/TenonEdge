namespace TenonEdge.Runtime.Deliveries;

/// <summary>Only frozen routing metadata is inspected; no payload, credentials or queue mutation.</summary>
public sealed record DeliveryRecoveryCheck(long CheckedCount, long BlockedCount, long[] BlockedConnections, bool MoreConnections);

internal sealed partial class DeliveryStore
{
    internal DeliveryRecoveryCheck CheckRecoveryTargets(Func<long, string, string, string?, bool> allows, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(allows);
        EnsureInitialized();
        using var connection = Open(_readConnectionString);
        using var transaction = connection.BeginTransaction(deferred: true);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // Stream bounded metadata from one snapshot. Quarantined records remain quarantined;
        // they cannot be implicitly released by recovery. Include failed records in the guard.
        command.CommandText = """
            SELECT connection_id, substr(fingerprint,1,1025), substr(device_key,1,129), substr(target_address,1,65536)
            FROM delivery WHERE state IN ('pending','failed');
            """;
        long total = 0, blocked = 0;
        var ids = new HashSet<long>();
        var more = false;
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                total++;
                var id = reader.GetInt64(0);
                if (allows(id, reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3)))
                { continue; }
                blocked++;
                if (ids.Count < 100 || ids.Contains(id))
                { ids.Add(id); }
                else
                { more = true; }
            }
        }
        transaction.Commit();
        return new(total, blocked, ids.Order().ToArray(), more);
    }
}
