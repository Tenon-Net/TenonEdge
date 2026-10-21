using Microsoft.Data.Sqlite;

namespace TenonEdge.Runtime.Deliveries;

internal sealed partial class DeliveryStore
{
    // Sender-owned prepared-head reader: owns a connection, never a cross-send transaction or body cache.
    internal PendingReader CreatePendingReader(long connectionId) => new(OpenReadConnection(), connectionId);

    internal sealed class PendingReader : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly SqliteCommand _command;
        private readonly SqliteParameter _after;
        private bool _disposed;

        internal PendingReader(SqliteConnection connection, long connectionId)
        {
            _connection = connection;
            _command = connection.CreateCommand();
            try
            {
                // Keep the native page-cache target small for many outputs; not a process-memory hard limit.
                using var settings = connection.CreateCommand();
                settings.CommandText = "PRAGMA cache_size = -64;";
                settings.ExecuteNonQuery();
                _command.CommandText = $"""
                    SELECT {SelectColumns} FROM delivery
                    WHERE connection_id = $connectionId AND state = 'pending' AND seq > $after
                    ORDER BY seq LIMIT 1;
                    """;
                _command.Parameters.AddWithValue("$connectionId", connectionId);
                _after = _command.Parameters.AddWithValue("$after", 0L);
                _command.Prepare();
            }
            catch
            {
                _command.Dispose();
                connection.Dispose();
                throw;
            }
        }

        internal StoredDelivery? Read(long after)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _after.Value = after;
            // One SELECT owns an implicit SQLite read transaction. Disposing its reader
            // resets the prepared statement and releases the snapshot, including on decoding errors.
            // No explicit BEGIN/COMMIT or read snapshot survives this method or a network send.
            using var reader = _command.ExecuteReader();
            return reader.Read() ? ReadRecord(reader) : null;
        }

        public void Dispose()
        {
            if (_disposed)
            { return; }
            _disposed = true;
            _command.Dispose();
            _connection.Dispose();
        }
    }
}
