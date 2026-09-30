using Microsoft.Data.Sqlite;
using TenonEdge.Host.Storage;
using TenonEdge.IntegrationTests.Infrastructure;

namespace TenonEdge.IntegrationTests.Storage;

/// <summary>
/// 配置库的连接设置与并发访问（V-06 开发环境部分，T0-05）：使用磁盘上的真实 SQLite 文件，不用内存数据库代替。
/// </summary>
public sealed class ConfigDatabaseTests(ITestOutputHelper output)
{
    [Fact]
    public async Task EveryPhysicalConnectionInThePoolReadsBackWalFullAndBusyTimeout()
    {
        using var store = TestConfigStore.Create();
        store.Initialize();
        const int concurrentConnections = 6;
        using var allOpen = new Barrier(concurrentConnections);

        // 每个分支的独立客户端关闭自动关闭连接并显式打开，使各自的物理连接同时处于借出状态，连接池只能分别打开不同的物理连接。
        // 不用事务占住连接：SqlSugar 经 Microsoft.Data.Sqlite 开始的事务是 BEGIN IMMEDIATE，一开始就取得写锁，多个事务只能排队。
        var results = await Task.WhenAll(Enumerable.Range(0, concurrentConnections).Select(_ => Task.Run(() =>
        {
            using var client = store.Database.CreateIsolatedClient();
            client.CurrentConnectionConfig.IsAutoCloseConnection = false;
            client.Ado.Open();
            try
            {
                var connection = (SqliteConnection)client.Ado.Connection;
                var state = SqliteConnectionSettings.Read(connection);
                var handle = connection.Handle!.DangerousGetHandle();
                var allArrived = allOpen.SignalAndWait(TimeSpan.FromSeconds(30));
                return (Handle: handle, State: state, AllArrived: allArrived);
            }
            finally
            {
                client.Ado.Close();
            }
        }, TestContext.Current.CancellationToken)));

        Assert.All(results, result => Assert.True(result.AllArrived, "各分支没有在 30 秒内同时持有连接。"));

        Assert.Equal(concurrentConnections, results.Select(result => result.Handle).Distinct().Count());
        foreach (var (handle, state, _) in results)
        {
            output.WriteLine($"物理连接 {handle}: journal_mode={state.JournalMode} synchronous={state.Synchronous} busy_timeout={state.BusyTimeoutMilliseconds}");
            Assert.Equal(SqliteConnectionSettings.ExpectedJournalMode, state.JournalMode);
            Assert.Equal(SqliteConnectionSettings.SynchronousFull, state.Synchronous);
            Assert.Equal(SqliteConnectionSettings.BusyTimeoutMilliseconds, state.BusyTimeoutMilliseconds);
        }
    }

    [Fact]
    public async Task ConcurrentWritesThroughIsolatedClientsAllSucceed()
    {
        using var store = TestConfigStore.Create();
        store.Initialize();
        store.Database.Client.Ado.ExecuteCommand("CREATE TABLE probe (id INTEGER PRIMARY KEY, value TEXT NOT NULL)");
        const int writers = 64;

        await Parallel.ForAsync(0, writers, TestContext.Current.CancellationToken, async (index, cancellationToken) =>
        {
            // 同一异步流程内的并发分支各用独立客户端（总设计 §2.3 的 CopyNew 策略）。
            using var client = store.Database.CreateIsolatedClient();
            await client.Ado.ExecuteCommandAsync("INSERT INTO probe (value) VALUES (@value)", new { value = $"row-{index}" }, cancellationToken);
            _ = await client.Ado.GetIntAsync("SELECT COUNT(*) FROM probe");
        });

        Assert.Equal(writers, store.Database.Client.Ado.GetInt("SELECT COUNT(*) FROM probe"));
    }

    [Fact]
    public void ConnectionWithoutWriteAheadLoggingIsRefusedInsteadOfUsed()
    {
        // 没有经过启动初始化、仍是默认 DELETE 日志模式的库：连接设置的回读校验不通过，操作失败，而不是在错误设置下写入。
        using var store = TestConfigStore.Create();

        var exception = Assert.ThrowsAny<Exception>(() => store.Database.Client.Ado.GetInt("SELECT 1"));

        Assert.Contains("配置库连接设置未生效", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ActuallyLoadedSqliteMeetsTheMinimumVersion()
    {
        using var store = TestConfigStore.Create();
        using var connection = store.Database.OpenInitializationConnection();

        var runtime = SqliteRuntimeInfo.Read(connection);

        output.WriteLine($"SQLite {runtime.Version}; Microsoft.Data.Sqlite {runtime.MicrosoftDataSqliteVersion}; SQLitePCLRaw {runtime.SqlitePclRawVersion}");
        output.WriteLine($"compile_options: {string.Join(",", runtime.CompileOptions)}");
        Assert.True(runtime.MeetsMinimumVersion, $"实际加载的 SQLite {runtime.Version} 低于 {SqliteRuntimeInfo.MinimumVersion}。");
        Assert.NotEmpty(runtime.CompileOptions);
    }

    [Fact]
    public void DatabaseFilesAreCreatedForOwnerOnly()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows 只用于开发调试，文件权限沿用目录继承的访问控制。");
        using var store = TestConfigStore.Create();

        store.Initialize();
        store.Database.Client.Ado.ExecuteCommand("CREATE TABLE probe (id INTEGER PRIMARY KEY)");

        foreach (var path in Directory.EnumerateFiles(store.DataDirectoryPath, ConfigDatabase.FileName + "*"))
        {
            Assert.Equal(PrivateFileSystem.FileMode, GetUnixFileMode(path));
        }
    }

    private static UnixFileMode GetUnixFileMode(string path) =>
        OperatingSystem.IsWindows() ? throw new PlatformNotSupportedException() : File.GetUnixFileMode(path);
}
