using Microsoft.Data.Sqlite;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.Runtime.Deliveries;

namespace TenonEdge.IntegrationTests.Runtime;

public sealed class DeliveryRecoveryTests
{
    [Fact]
    public void InvalidDatabaseIsPreservedAndRebuiltWithoutTouchingConfiguration()
    {
        using var data = TemporaryDirectory.Create();
        var path = Path.Combine(data.Path, "queue.db");
        var damaged = "not-a-sqlite-database"u8.ToArray();
        File.WriteAllBytes(path, damaged);
        File.WriteAllText(Path.Combine(data.Path, "config.db"), "configuration-sentinel");
        var events = new RecordingEventSink();
        var cache = Create(data.Path, events);
        try
        {
            Assert.Empty(cache.ReadPending(1, 0, 10));
            Assert.Empty(cache.GetPersistentCounters());
            var archive = Assert.Single(Directory.GetDirectories(data.Path, "queue.db.corrupt-*"));
            Assert.Equal(damaged, File.ReadAllBytes(Path.Combine(archive, "queue.db")));
            Assert.Equal("configuration-sentinel", File.ReadAllText(Path.Combine(data.Path, "config.db")));
            Assert.Contains("数量未知", Assert.Single(events.OfType("storage.queueRebuilt")).Description, StringComparison.Ordinal);
            Assert.Empty(Directory.GetDirectories(data.Path, "queue.db.recovering"));
            Assert.Empty(cache.ReadPending(1, 0, 10));
            Assert.Single(events.OfType("storage.queueRebuilt"));
        }
        finally { cache.Store.ClearReadPool(); }
    }

    [Fact]
    public async Task DamagedPageIsDetectedAndReadableDeliveryCountIsReported()
    {
        using var data = TemporaryDirectory.Create();
        await using (var runtime = RuntimeTesting.CreateRuntime([], data.Path))
        {
            runtime.StartCore();
            var draft = new DeliveryDraft(Guid.NewGuid(), 1, Guid.NewGuid(), "meter", 1, "{}"u8.ToArray(), "fp", "target");
            Assert.True((await runtime.Deliveries.EnqueueAsync([draft], TestContext.Current.CancellationToken)).Committed);
            await runtime.StopAsync(TestContext.Current.CancellationToken);
        }
        var path = Path.Combine(data.Path, "queue.db");
        long root;
        long pageSize;
        using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE damaged (value TEXT); INSERT INTO damaged VALUES ('test'); PRAGMA wal_checkpoint(TRUNCATE);";
            command.ExecuteNonQuery();
            command.CommandText = "SELECT rootpage FROM sqlite_master WHERE name='damaged'";
            root = (long)command.ExecuteScalar()!;
            command.CommandText = "PRAGMA page_size";
            pageSize = (long)command.ExecuteScalar()!;
        }
        using (var file = File.OpenWrite(path))
        {
            file.Position = (root - 1) * pageSize;
            file.WriteByte(0); // 非法 b-tree 页头；delivery 表仍可读取。
        }
        var events = new RecordingEventSink();
        var cache = Create(data.Path, events);
        try
        {
            Assert.Empty(cache.ReadPending(1, 0, 10));
            Assert.Contains("记录数：1", Assert.Single(events.OfType("storage.queueRebuilt")).Description, StringComparison.Ordinal);
            Assert.Single(Directory.GetDirectories(data.Path, "queue.db.corrupt-*"));
        }
        finally { cache.Store.ClearReadPool(); }
    }

    [Fact]
    public void InterruptedMoveFinishesSidecarsBeforeCreatingNewDatabase()
    {
        using var data = TemporaryDirectory.Create();
        var path = Path.Combine(data.Path, "queue.db");
        var staging = Directory.CreateDirectory(path + ".recovering").FullName;
        File.WriteAllText(Path.Combine(staging, "queue.db"), "damaged-main");
        File.WriteAllText(Path.Combine(staging, "count.txt"), "7");
        File.WriteAllText(path + "-wal", "old-wal");
        File.WriteAllText(path + "-shm", "old-shm");
        var events = new RecordingEventSink();
        var cache = Create(data.Path, events);
        try
        {
            Assert.Empty(cache.ReadPending(1, 0, 10));
            var archive = Assert.Single(Directory.GetDirectories(data.Path, "queue.db.corrupt-*"));
            Assert.Equal("old-wal", File.ReadAllText(Path.Combine(archive, "queue.db-wal")));
            Assert.Equal("old-shm", File.ReadAllText(Path.Combine(archive, "queue.db-shm")));
            Assert.Contains("记录数：7", Assert.Single(events.OfType("storage.queueRebuilt")).Description, StringComparison.Ordinal);
        }
        finally { cache.Store.ClearReadPool(); }
    }

    [Fact]
    public void MoveFailureDoesNotOverwriteEvidenceOrCreateANewDatabase()
    {
        using var data = TemporaryDirectory.Create();
        var path = Path.Combine(data.Path, "queue.db");
        var staging = Directory.CreateDirectory(path + ".recovering").FullName;
        File.WriteAllText(Path.Combine(staging, "queue.db"), "preserved");
        File.WriteAllText(path, "not-moved");
        var cache = Create(data.Path, new RecordingEventSink());
        Assert.Throws<IOException>(() => cache.ReadPending(1, 0, 10));
        Assert.Equal("preserved", File.ReadAllText(Path.Combine(staging, "queue.db")));
        Assert.Equal("not-moved", File.ReadAllText(path));
    }

    [Fact]
    public void NewerSchemaIsNotTreatedAsCorruption()
    {
        using var data = TemporaryDirectory.Create();
        var cache = Create(data.Path, new RecordingEventSink());
        cache.Store.EnsureInitialized();
        cache.Store.ClearReadPool();
        using (var connection = new SqliteConnection($"Data Source={Path.Combine(data.Path, "queue.db")};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version=999";
            command.ExecuteNonQuery();
        }
        var reopened = Create(data.Path, new RecordingEventSink());
        Assert.Throws<DeliveryStoreException>(() => reopened.Store.EnsureInitialized());
        Assert.Empty(Directory.GetDirectories(data.Path, "queue.db.*"));
    }

    private static DeliveryCache Create(string directory, RecordingEventSink events) => new(new DeliveryCacheOptions
    {
        DatabasePath = Path.Combine(directory, "queue.db"),
        ConfigStoreId = RuntimeTesting.ConfigStoreId,
    }, TimeProvider.System, events: events);
}
