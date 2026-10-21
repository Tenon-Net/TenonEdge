using Microsoft.Data.Sqlite;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Host.Storage;

namespace TenonEdge.Host.Features.Deliveries;

internal sealed partial class OutputControlStore
{
    public IOutputControlReadSession OpenSenderReadSession() => new SenderReader(database);

    internal sealed class SenderReader : IOutputControlReadSession
    {
        internal SqliteConnection Connection { get; }
        private readonly SqliteCommand _command;
        private readonly SqliteParameter _id;
        private bool _disposed;

        internal SenderReader(ConfigDatabase database)
        {
            Connection = database.OpenSenderReadConnection();
            _command = Connection.CreateCommand();
            try
            {
                _command.CommandText = "SELECT manually_paused, version FROM output_control WHERE connection_id = @id";
                _id = _command.Parameters.Add("@id", SqliteType.Integer);
                _id.Value = 0L;
                _command.Prepare();
            }
            catch { Dispose(); throw; }
        }

        public OutputControlState Read(long connectionId)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _id.Value = connectionId;
            using var reader = _command.ExecuteReader();
            return !reader.Read() ? new(false, null) : new(reader.GetBoolean(0), reader.GetString(1));
        }

        public void Dispose()
        {
            if (_disposed)
            { return; }
            _disposed = true;
            try
            { _command.Dispose(); }
            finally { Connection.Dispose(); }
        }
    }
}
