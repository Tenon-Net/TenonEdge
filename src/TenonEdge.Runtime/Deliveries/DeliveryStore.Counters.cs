using Microsoft.Data.Sqlite;

namespace TenonEdge.Runtime.Deliveries;

internal sealed partial class DeliveryStore
{
    internal static void AddCounter(SqliteConnection connection, SqliteTransaction transaction, string name, long count)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO queue_counters (name, value) VALUES ($name, $count)
            ON CONFLICT(name) DO UPDATE SET value = value + excluded.value;
            """;
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$count", count);
        command.ExecuteNonQuery();
    }

    internal IReadOnlyDictionary<string, long> ReadCounters()
    {
        EnsureInitialized();
        using var connection = Open(_readConnectionString);
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
