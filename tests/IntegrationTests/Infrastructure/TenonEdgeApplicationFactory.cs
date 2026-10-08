using System.Collections.Concurrent;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
/// <paramref name="settings"/> 是额外的部署参数（例如运行时的种子快照），每次启动可以不同；
/// <paramref name="configureServices"/> 用于登记测试用的服务（例如测试用的输入工厂）。
/// </summary>
public sealed class FixedDataDirectoryApplicationFactory(
    string dataDirectory,
    IReadOnlyDictionary<string, string>? settings = null,
    Action<IServiceCollection>? configureServices = null) : TenonEdgeApplicationFactory
{
    private static readonly TimeSpan LockReleaseTimeout = TimeSpan.FromSeconds(30);

    private IHost? _host;

    /// <summary>
    /// 宿主由 Program 中的 RunAsync 与工厂两处停止和释放：后到的一方发现已在释放便立即返回，而另一方可能仍在停止后台服务、释放单例，
    /// 数据目录锁最后释放（关闭配置库连接时还要做一次 WAL 检查点）。因此先等宿主发出“已停止”（全部后台服务的停止都已完成，
    /// 例如运行时协调者清除“运行中”标记），再释放，最后等到锁可以取得再返回。之后在同一目录上启动的宿主、维护命令或测试对数据库的修改，
    /// 都不会与上一个宿主仍在进行的停止交错。
    /// </summary>
    public override async ValueTask DisposeAsync()
    {
        await WaitForHostStoppedAsync();
        await base.DisposeAsync();
        await WaitForDataDirectoryLockAsync();
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        var host = base.CreateHost(builder);
        _host = host;
        return host;
    }

    protected override string CreateDataDirectory() => dataDirectory;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        foreach (var (key, value) in settings ?? new Dictionary<string, string>())
        {
            builder.UseSetting(key, value);
        }

        if (configureServices is not null)
        {
            builder.ConfigureTestServices(configureServices);
        }
    }

    private async Task WaitForHostStoppedAsync()
    {
        if (_host is null)
        {
            return;
        }

        IHostApplicationLifetime lifetime;
        try
        {
            lifetime = _host.Services.GetRequiredService<IHostApplicationLifetime>();
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (lifetime.ApplicationStopped.Register(() => stopped.TrySetResult()))
        {
            await _host.StopAsync();
            await stopped.Task.WaitAsync(LockReleaseTimeout);
        }
    }

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
