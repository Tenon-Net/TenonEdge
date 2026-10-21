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
}
