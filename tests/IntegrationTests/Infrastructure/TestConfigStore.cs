using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using TenonEdge.Host.Bootstrap;
using TenonEdge.Host.Storage;
using TenonEdge.Host.Storage.Migrations;

namespace TenonEdge.IntegrationTests.Infrastructure;

/// <summary>
/// 在独立的临时数据目录上，按宿主相同的方式取得单实例锁并打开配置库，供存储测试直接使用。
/// 释放时先关闭配置库的全部物理连接，再释放锁；自己创建的目录一并删除。
/// </summary>
public sealed class TestConfigStore : IDisposable
{
    private readonly TemporaryDirectory? _ownedDirectory;
    private readonly DataDirectoryLock _dataDirectoryLock;

    private TestConfigStore(string root, TemporaryDirectory? ownedDirectory)
    {
        _ownedDirectory = ownedDirectory;
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [DataDirectory.ConfigurationKey] = root })
            .Build();
        _dataDirectoryLock = DataDirectoryLock.Acquire(
            DataDirectory.Resolve(configuration, new HostingEnvironment { EnvironmentName = Environments.Production }));
        Database = new ConfigDatabase(_dataDirectoryLock);
    }

    public string DataDirectoryPath => _dataDirectoryLock.Directory.Root;

    internal ConfigDatabase Database { get; }

    internal DataDirectoryLock DataDirectoryLock => _dataDirectoryLock;

    internal RuntimeStartConditions RuntimeStartConditions { get; } = new();

    public static TestConfigStore Create()
    {
        var directory = TemporaryDirectory.Create();
        try
        {
            return new TestConfigStore(directory.Path, directory);
        }
        catch
        {
            directory.Dispose();
            throw;
        }
    }

    /// <summary>在已有目录上打开（例如由另一个 TestConfigStore 释放后留下的目录），释放时不删除目录。</summary>
    public static TestConfigStore Open(string existingDirectory) => new(existingDirectory, ownedDirectory: null);

    /// <summary>按宿主启动时的步骤初始化：记录 SQLite 版本、启用 WAL、执行指定（默认为本程序）的迁移。</summary>
    internal void Initialize(IReadOnlyList<ConfigMigration>? migrations = null, TimeProvider? timeProvider = null) =>
        new ConfigStoreInitializer(
            Database,
            CreateMigrator(migrations, timeProvider),
            RuntimeStartConditions,
            NullLogger<ConfigStoreInitializer>.Instance).Initialize();

    internal ConfigMigrator CreateMigrator(IReadOnlyList<ConfigMigration>? migrations = null, TimeProvider? timeProvider = null) =>
        new(Database, migrations ?? ConfigMigrations.All, timeProvider ?? TimeProvider.System, NullLogger<ConfigMigrator>.Instance);

    public void Dispose()
    {
        Database.Dispose();
        _dataDirectoryLock.Dispose();
        _ownedDirectory?.Dispose();
    }
}
