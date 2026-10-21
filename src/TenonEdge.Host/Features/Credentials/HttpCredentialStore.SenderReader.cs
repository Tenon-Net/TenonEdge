using Microsoft.Data.Sqlite;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Host.Storage;

namespace TenonEdge.Host.Features.Credentials;

internal sealed partial class HttpCredentialStore
{
    public IHttpCredentialReadSession OpenSenderReadSession() => new SenderReader(database, this);

    // 候选：仅复用连接与语句，不保留身份、秘密或事务。秘密解析始终交回普通 SqlSugar 路径。
    internal sealed class SenderReader : IHttpCredentialReadSession
    {
        internal SqliteConnection Connection { get; }
        private readonly SqliteCommand _command;
        private readonly SqliteParameter _id;
        private readonly IHttpCredentialResolver _source;
        private bool _disposed;

        internal SenderReader(ConfigDatabase database, IHttpCredentialResolver source)
        {
            _source = source;
            Connection = database.OpenSenderReadConnection();
            _command = Connection.CreateCommand();
            try
            {
                _command.CommandText = """
                    SELECT kind, username, name, account_marker, version, certificate_subject
                    FROM http_credential WHERE connection_id = @id
                    """;
                _id = _command.Parameters.Add("@id", SqliteType.Integer);
                _id.Value = 0L;
                _command.Prepare();
            }
            catch { Dispose(); throw; }
        }

        public HttpCredentialIdentity? DescribeHttp(long connectionId)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _id.Value = connectionId;
            // SELECT 的隐式短事务随 reader 释放，异常也释放；下一次重新读当前已提交值。
            using var reader = _command.ExecuteReader();
            return !reader.Read() ? null : new((HttpCredentialKind)reader.GetInt32(0),
                reader.IsDBNull(1) ? null : reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetString(3), reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5));
        }

        public HttpCredential? ResolveHttp(long connectionId, string recipient)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _source.ResolveHttp(connectionId, recipient);
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
