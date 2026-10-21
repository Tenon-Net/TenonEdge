using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Host.Features.Credentials;
using TenonEdge.Host.Features.Deliveries;
using TenonEdge.Host.Storage;
using TenonEdge.IntegrationTests.Infrastructure;

namespace TenonEdge.IntegrationTests.Storage;

public sealed class SenderConfigurationReadTests
{
    [Fact]
    public void DedicatedConnectionIsReadOnlyIndependentAndKeepsTheRequiredSettings()
    {
        using var store = TestConfigStore.Create();
        store.Initialize();
        using var first = store.Database.OpenSenderReadConnection();
        using var second = store.Database.OpenSenderReadConnection();
        Assert.NotEqual(first.Handle!.DangerousGetHandle(), second.Handle!.DangerousGetHandle());
        foreach (var connection in new[] { first, second })
        {
            var settings = new SqliteConnectionStringBuilder(connection.ConnectionString);
            Assert.Equal(SqliteOpenMode.ReadOnly, settings.Mode);
            Assert.False(settings.Pooling);
            Assert.Equal(ConfigDatabase.CommandTimeoutSeconds, connection.DefaultTimeout);
            Assert.Equal(new("wal", 2, 5000), SqliteConnectionSettings.Read(connection));
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA query_only";
            Assert.Equal(1L, command.ExecuteScalar());
            command.CommandText = "PRAGMA foreign_keys";
            Assert.Equal(1L, command.ExecuteScalar());
            command.CommandText = "PRAGMA read_uncommitted";
            Assert.Equal(0L, command.ExecuteScalar());
            command.CommandText = "CREATE TABLE forbidden_sender_write (id INTEGER)";
            Assert.Equal(8, Assert.Throws<SqliteException>(() => command.ExecuteNonQuery()).SqliteErrorCode);
            // 即便意外关闭 query_only，底层只读打开模式也拒绝写入。
            command.CommandText = "PRAGMA query_only = OFF";
            command.ExecuteNonQuery();
            command.CommandText = "CREATE TABLE forbidden_sender_write (id INTEGER)";
            Assert.Equal(8, Assert.Throws<SqliteException>(() => command.ExecuteNonQuery()).SqliteErrorCode);
        }
        first.Dispose();
        using var stillOpen = second.CreateCommand();
        stillOpen.CommandText = "SELECT 42";
        Assert.Equal(42L, stillOpen.ExecuteScalar());
        Assert.Equal(43, store.Database.Client.Ado.GetInt("SELECT 43"));
    }

    [Fact]
    public void UninitializedDatabaseCannotUseTheDedicatedConnectionAndDoesNotLeakItsHandle()
    {
        using var store = TestConfigStore.Create();
        Assert.Throws<SqliteException>(() => store.Database.OpenSenderReadConnection());
        using (var connection = store.Database.OpenInitializationConnection())
        {
            using var create = connection.CreateCommand();
            create.CommandText = "CREATE TABLE probe (id INTEGER)";
            create.ExecuteNonQuery();
        }
        Assert.Throws<InvalidOperationException>(() => store.Database.OpenSenderReadConnection());
        store.Initialize();
        using var opened = store.Database.OpenSenderReadConnection();
        Assert.Equal(new("wal", 2, 5000), SqliteConnectionSettings.Read(opened));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreparedReadSeesOnlyCommitsAndReleasesItsSnapshotBetweenCalls(bool identity)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var database = factory.Services.GetRequiredService<ConfigDatabase>();
        var (session, connection, read) = Open(factory, identity);
        using var owned = session;
        using var writer = database.CreateIsolatedClient();
        writer.Insertable(new HttpCredentialRecord
        {
            ConnectionId = 1,
            Kind = (int)HttpCredentialKind.Basic,
            AccountMarker = "account",
            Version = "old",
            ProtectedSecret = "invalid-fixture-ciphertext"
        }).ExecuteCommand();
        writer.Insertable(new OutputControlRecord { ConnectionId = 1, Version = "old" }).ExecuteCommand();
        Assert.Equal("old", read());
        writer.Ado.BeginTran();
        try
        {
            writer.Ado.ExecuteCommand("UPDATE http_credential SET version = 'new' WHERE connection_id = 1");
            writer.Ado.ExecuteCommand("UPDATE output_control SET version = 'new', manually_paused = 1 WHERE connection_id = 1");
            Assert.Equal("old", read());
            writer.Ado.RollbackTran();
            Assert.Equal("old", read());
            writer.Ado.BeginTran();
            writer.Ado.ExecuteCommand("UPDATE http_credential SET version = 'new' WHERE connection_id = 1");
            writer.Ado.ExecuteCommand("UPDATE output_control SET version = 'new', manually_paused = 1 WHERE connection_id = 1");
            writer.Ado.CommitTran();
        }
        catch { writer.Ado.RollbackTran(); throw; }

        List<string> statements = [];
        SQLitePCL.raw.sqlite3_trace(connection.Handle, (_, sql) => statements.Add(sql), null);
        Assert.Equal("new", read());
        Assert.Equal(1, SQLitePCL.raw.sqlite3_get_autocommit(connection.Handle));
        Assert.StartsWith("SELECT ", Assert.Single(statements), StringComparison.Ordinal);
        Assert.DoesNotContain("protected_secret", statements[0], StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("recipient", statements[0], StringComparison.OrdinalIgnoreCase);
        if (!identity)
        { Assert.True(((IOutputControl)session).Read(1).ManuallyPaused); }
        AssertCheckpoint(database);
        session.Dispose();
        session.Dispose();
        Assert.Throws<ObjectDisposedException>(() => read());
        Assert.Equal(System.Data.ConnectionState.Closed, connection.State);
        // 监督重建必须得到新读取器，而不是复活已释放的实例。
        var (next, _, nextRead) = Open(factory, identity);
        using var rebuilt = next;
        Assert.Equal("new", nextRead());
    }

    [Fact]
    public async Task IdentityDecodingFailureReleasesSnapshotAndNextReadSeesRepairedMetadata()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var database = factory.Services.GetRequiredService<ConfigDatabase>();
        var credentials = factory.Services.GetRequiredService<HttpCredentialStore>();
        using var reader = credentials.OpenSenderReadSession();
        using var writer = database.CreateIsolatedClient();
        writer.CurrentConnectionConfig.IsAutoCloseConnection = false;
        writer.Ado.Open();
        // 仅测试自己的物理连接临时绕过 CHECK，模拟落盘损坏；不修改产品 schema 或安全设置。
        writer.Ado.ExecuteCommand("PRAGMA ignore_check_constraints = ON");
        writer.Ado.ExecuteCommand("INSERT INTO http_credential (connection_id, recipient, kind, account_marker, version, updated_at) VALUES (1, 'https://example.invalid/', 2147483648, 'account', 'v1', 0)");
        writer.Ado.ExecuteCommand("PRAGMA ignore_check_constraints = OFF");
        Assert.Throws<OverflowException>(() => reader.DescribeHttp(1));
        writer.Ado.ExecuteCommand("UPDATE http_credential SET kind = 0, version = 'repaired' WHERE connection_id = 1");
        AssertCheckpoint(database);
        Assert.Equal("repaired", reader.DescribeHttp(1)!.Version);
    }

    [Fact]
    public void DedicatedMetadataReaderStillResolvesSecretsThroughTheOriginalResolverEveryTime()
    {
        using var store = TestConfigStore.Create();
        store.Initialize();
        var source = new ChangingSecret();
        using var reader = new HttpCredentialStore.SenderReader(store.Database, source);
        Assert.Null(reader.DescribeHttp(1));
        Assert.Equal("fixture-secret-1", reader.ResolveHttp(1, "https://example.invalid/")!.Secret);
        Assert.Equal("fixture-secret-2", reader.ResolveHttp(1, "https://example.invalid/")!.Secret);
        reader.Dispose();
        Assert.Throws<ObjectDisposedException>(() => reader.ResolveHttp(1, "https://example.invalid/"));
        Assert.Equal(2, source.Reads);
    }

    private sealed class ChangingSecret : IHttpCredentialResolver
    {
        public int Reads { get; private set; }
        public HttpCredentialIdentity? DescribeHttp(long connectionId) => throw new InvalidOperationException("Must read metadata from the database");
        public HttpCredential ResolveHttp(long connectionId, string recipient)
        {
            Assert.Equal(1, connectionId);
            Assert.Equal("https://example.invalid/", recipient);
            return new(new(HttpCredentialKind.Bearer, null, null, "fixture-account", $"version-{++Reads}"), $"fixture-secret-{Reads}");
        }
    }

    private static (IDisposable Session, SqliteConnection Connection, Func<string?> Read) Open(TenonEdgeApplicationFactory factory, bool identity)
    {
        if (identity)
        {
            var session = Assert.IsType<HttpCredentialStore.SenderReader>(factory.Services.GetRequiredService<HttpCredentialStore>().OpenSenderReadSession());
            return (session, session.Connection, () => session.DescribeHttp(1)?.Version);
        }
        var controls = Assert.IsType<OutputControlStore.SenderReader>(factory.Services.GetRequiredService<OutputControlStore>().OpenSenderReadSession());
        return (controls, controls.Connection, () => controls.Read(1).ResumeVersion);
    }

    private static void AssertCheckpoint(ConfigDatabase database)
    {
        using var connection = database.OpenInitializationConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
        using var result = command.ExecuteReader();
        Assert.True(result.Read());
        Assert.Equal(0, result.GetInt64(0));
    }
}
