using System.Text.RegularExpressions;
using Microsoft.Extensions.Hosting;
using TenonEdge.Host.Bootstrap;
using TenonEdge.Host.Storage;
using TenonEdge.Host.Storage.Migrations;
using TenonEdge.IntegrationTests.Infrastructure;

namespace TenonEdge.IntegrationTests.Processes;

/// <summary>
/// 配置库的真实进程验证（T0-05）：启动日志记录实际加载的 SQLite 版本（V-07 开发环境部分）；
/// 配置库损坏或已由更新版本的程序迁移时，宿主以退出码 4 结束并说明原因，不开始监听。
/// </summary>
public sealed partial class ConfigStoreProcessTests(ITestOutputHelper output)
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    [Trait("Category", "Process")]
    public async Task StartupLogRecordsTheActuallyLoadedSqliteVersion()
    {
        SkipUnlessLinux();
        var cancellationToken = TestContext.Current.CancellationToken;
        using var workingDirectory = TemporaryDirectory.Create();
        using var dataDirectory = TemporaryDirectory.Create();

        await using var host = HostProcess.Start(workingDirectory.Path, Environments.Production, dataDirectory.Path);
        try
        {
            var address = await host.WaitForListeningAddressAsync(StartupTimeout, cancellationToken);
            using var client = new HttpClient { BaseAddress = address, Timeout = TimeSpan.FromSeconds(10) };
            await host.WaitUntilReadyAsync(client, StartupTimeout, cancellationToken);

            var match = SqliteVersionLine().Match(host.Output);
            Assert.True(match.Success, "启动日志中没有 SQLite 版本记录。");
            Assert.True(
                SqliteRuntimeInfo.IsSupportedVersion(match.Groups["version"].Value),
                $"实际加载的 SQLite {match.Groups["version"].Value} 低于 {SqliteRuntimeInfo.MinimumVersion}。");
            Assert.Contains("编译选项：", host.Output, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(dataDirectory.Path, ConfigDatabase.FileName)));

            await host.RequestStopAsync(cancellationToken);
            Assert.Equal(HostExitCodes.Success, await host.WaitForExitAsync(ExitTimeout, cancellationToken));
        }
        finally
        {
            output.WriteLine(host.Output);
        }
    }

    [Fact]
    [Trait("Category", "Process")]
    public async Task CorruptConfigStoreStopsHostWithConfigStoreExitCode()
    {
        SkipUnlessLinux();
        var cancellationToken = TestContext.Current.CancellationToken;
        using var workingDirectory = TemporaryDirectory.Create();
        using var dataDirectory = TemporaryDirectory.Create();
        await File.WriteAllTextAsync(
            Path.Combine(dataDirectory.Path, ConfigDatabase.FileName),
            new string('x', 8192),
            cancellationToken);

        await using var host = HostProcess.Start(workingDirectory.Path, Environments.Production, dataDirectory.Path);
        try
        {
            Assert.Equal(HostExitCodes.ConfigStoreUnavailable, await host.WaitForExitAsync(ExitTimeout, cancellationToken));
            Assert.Contains("无法打开配置库", host.Output, StringComparison.Ordinal);
            Assert.DoesNotContain("Now listening on", host.Output, StringComparison.Ordinal);
        }
        finally
        {
            output.WriteLine(host.Output);
        }
    }

    [Fact]
    [Trait("Category", "Process")]
    public async Task ConfigStoreMigratedByNewerProgramStopsHost()
    {
        SkipUnlessLinux();
        var cancellationToken = TestContext.Current.CancellationToken;
        using var workingDirectory = TemporaryDirectory.Create();
        using var dataDirectory = TemporaryDirectory.Create();
        using (var newerProgram = TestConfigStore.Open(dataDirectory.Path))
        {
            var nextVersion = ConfigMigrations.All[^1].Version + 1;
            newerProgram.Initialize([.. ConfigMigrations.All, new ConfigMigration(nextVersion, "from_newer_program", ["CREATE TABLE newer (id INTEGER PRIMARY KEY)"])]);
        }

        await using var host = HostProcess.Start(workingDirectory.Path, Environments.Production, dataDirectory.Path);
        try
        {
            Assert.Equal(HostExitCodes.ConfigStoreUnavailable, await host.WaitForExitAsync(ExitTimeout, cancellationToken));
            Assert.Contains("不支持降级", host.Output, StringComparison.Ordinal);
            Assert.DoesNotContain("Now listening on", host.Output, StringComparison.Ordinal);
        }
        finally
        {
            output.WriteLine(host.Output);
        }
    }

    private static void SkipUnlessLinux() =>
        Assert.SkipUnless(OperatingSystem.IsLinux(), "真实进程测试依赖 SIGTERM 与 flock，只在 Linux 上执行；首版发布目标为 Linux x64，CI 在 Linux 上执行。");

    [GeneratedRegex(@"SQLite 原生库版本 (?<version>\d+(\.\d+)+)")]
    private static partial Regex SqliteVersionLine();
}
