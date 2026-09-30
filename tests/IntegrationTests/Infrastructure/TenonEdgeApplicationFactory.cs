using System.Collections.Concurrent;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using TenonEdge.Host.Bootstrap;
using TenonEdge.Host.Storage;

namespace TenonEdge.IntegrationTests.Infrastructure;

/// <summary>
/// 在进程内启动实际的宿主装配（Program）。
/// <list type="bullet">
/// <item>WebApplicationFactory 默认使用 Development 环境；这里默认改为 Production，使错误处理、匿名例外等行为与正式运行一致。</item>
/// <item>每个宿主使用独立的临时数据目录。WithWebHostBuilder 派生的宿主也会调用本类的 ConfigureWebHost，因而同样得到自己的目录；
/// 同一数据目录只允许一个宿主持有单实例锁。目录在工厂释放时删除。</item>
/// </list>
/// </summary>
public class TenonEdgeApplicationFactory : WebApplicationFactory<Program>
{
    private readonly ConcurrentBag<TemporaryDirectory> _dataDirectories = [];

    protected virtual string EnvironmentName => Environments.Production;

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        DeleteDataDirectories();
        GC.SuppressFinalize(this);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(EnvironmentName);
        builder.UseSetting(DataDirectory.ConfigurationKey, CreateDataDirectory());
    }

    /// <summary>为即将构建的宿主提供数据目录；默认每次新建一个临时目录，工厂释放时删除。</summary>
    protected virtual string CreateDataDirectory()
    {
        var temporary = TemporaryDirectory.Create();
        _dataDirectories.Add(temporary);
        return temporary.Path;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            DeleteDataDirectories();
        }
    }

    private void DeleteDataDirectories()
    {
        while (_dataDirectories.TryTake(out var directory))
        {
            directory.Dispose();
        }
    }
}

/// <summary>
/// 使用指定的数据目录，释放时不删除，用于同一数据目录上的重启测试。
/// 同一时刻只能有一个宿主使用该目录，因此不要在宿主运行期间再派生第二个宿主。
/// </summary>
public sealed class FixedDataDirectoryApplicationFactory(string dataDirectory) : TenonEdgeApplicationFactory
{
    private static readonly TimeSpan LockReleaseTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 宿主由 Program 中的 RunAsync 与工厂两处释放：后到的一方发现已在释放便立即返回，而另一方可能仍在释放单例，
    /// 数据目录锁最后释放（关闭配置库连接时还要做一次 WAL 检查点）。等到锁可以取得再返回，
    /// 之后在同一目录上启动的宿主或维护命令就不会因锁仍被占用而失败。
    /// </summary>
    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await WaitForDataDirectoryLockAsync();
    }

    protected override string CreateDataDirectory() => dataDirectory;

    private async Task WaitForDataDirectoryLockAsync()
    {
        if (!Directory.Exists(dataDirectory))
        {
            return;
        }

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [DataDirectory.ConfigurationKey] = dataDirectory })
            .Build();
        var directory = DataDirectory.Resolve(configuration, new HostingEnvironment { EnvironmentName = Environments.Production });
        var deadline = DateTimeOffset.UtcNow + LockReleaseTimeout;
        while (true)
        {
            try
            {
                using var probe = DataDirectoryLock.Acquire(directory);
                return;
            }
            catch (HostStartupException exception) when (exception.ExitCode == HostExitCodes.DataDirectoryInUse && DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(20));
            }
        }
    }
}

/// <summary>以 Development 环境启动，用于核对只在开发环境开放的行为。</summary>
public sealed class DevelopmentApplicationFactory : TenonEdgeApplicationFactory
{
    protected override string EnvironmentName => Environments.Development;
}
