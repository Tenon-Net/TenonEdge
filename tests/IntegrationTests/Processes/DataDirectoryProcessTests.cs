using System.Text.RegularExpressions;
using Microsoft.Extensions.Hosting;
using TenonEdge.Host.Bootstrap;
using TenonEdge.Host.Storage;
using TenonEdge.IntegrationTests.Infrastructure;

namespace TenonEdge.IntegrationTests.Processes;

/// <summary>
/// 数据目录与单实例锁的真实进程验证（T0-04，AT-10 单实例子项）：
/// 同一目录的第二个实例被拒绝；正常停止或被强制终止后可以重新取锁；工作目录不影响数据位置；路径错误明确报告。
/// </summary>
public sealed partial class DataDirectoryProcessTests(ITestOutputHelper output)
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    [Trait("Category", "Process")]
    public async Task SecondInstanceOnSameDataDirectoryIsRejectedWhileFirstKeepsRunning()
    {
        SkipUnlessLinux();
        var cancellationToken = TestContext.Current.CancellationToken;
        using var workingDirectory = TemporaryDirectory.Create();
        using var dataDirectory = TemporaryDirectory.Create();

        await using var first = HostProcess.Start(workingDirectory.Path, Environments.Production, dataDirectory.Path);
        try
        {
            using var client = await WaitUntilReadyAsync(first, cancellationToken);

            // 第一个实例就绪、已持有锁之后才启动第二个，结果与启动先后的竞争无关。
            await using var second = HostProcess.Start(workingDirectory.Path, Environments.Production, dataDirectory.Path);
            try
            {
                Assert.Equal(HostExitCodes.DataDirectoryInUse, await second.WaitForExitAsync(ExitTimeout, cancellationToken));
                Assert.Contains("正被另一个 TenonEdge 进程使用", second.Output, StringComparison.Ordinal);
                Assert.DoesNotContain("Now listening on", second.Output, StringComparison.Ordinal);
            }
            finally
            {
                output.WriteLine(second.Output);
            }

            // 被拒绝的实例没有影响正在运行的实例。
            await first.WaitUntilReadyAsync(client, StartupTimeout, cancellationToken);
            await first.RequestStopAsync(cancellationToken);
            Assert.Equal(HostExitCodes.Success, await first.WaitForExitAsync(ExitTimeout, cancellationToken));
        }
        finally
        {
            output.WriteLine(first.Output);
        }
    }

    [Fact]
    [Trait("Category", "Process")]
    public async Task LockIsReleasedAfterHolderIsKilledOrStopped()
    {
        SkipUnlessLinux();
        var cancellationToken = TestContext.Current.CancellationToken;
        using var workingDirectory = TemporaryDirectory.Create();
        using var dataDirectory = TemporaryDirectory.Create();

        // 强制终止：进程没有机会执行任何清理，锁由操作系统释放。
        await using (var killed = HostProcess.Start(workingDirectory.Path, Environments.Production, dataDirectory.Path))
        {
            using var client = await WaitUntilReadyAsync(killed, cancellationToken);
            await killed.KillAsync(cancellationToken);
            output.WriteLine(killed.Output);
        }

        await using (var stopped = HostProcess.Start(workingDirectory.Path, Environments.Production, dataDirectory.Path))
        {
            using var client = await WaitUntilReadyAsync(stopped, cancellationToken);
            await stopped.RequestStopAsync(cancellationToken);
            Assert.Equal(HostExitCodes.Success, await stopped.WaitForExitAsync(ExitTimeout, cancellationToken));
            output.WriteLine(stopped.Output);
        }

        await using var next = HostProcess.Start(workingDirectory.Path, Environments.Production, dataDirectory.Path);
        using (var client = await WaitUntilReadyAsync(next, cancellationToken))
        {
            await next.RequestStopAsync(cancellationToken);
            Assert.Equal(HostExitCodes.Success, await next.WaitForExitAsync(ExitTimeout, cancellationToken));
        }

        output.WriteLine(next.Output);
    }

    [Fact]
    [Trait("Category", "Process")]
    public async Task WorkingDirectoryDoesNotSelectWhereDataIsWritten()
    {
        SkipUnlessLinux();
        var cancellationToken = TestContext.Current.CancellationToken;
        using var firstWorkingDirectory = TemporaryDirectory.Create();
        using var secondWorkingDirectory = TemporaryDirectory.Create();
        using var dataDirectory = TemporaryDirectory.Create();

        var nodeIds = new List<string>();
        foreach (var workingDirectory in new[] { firstWorkingDirectory, secondWorkingDirectory })
        {
            await using var host = HostProcess.Start(workingDirectory.Path, Environments.Production, dataDirectory.Path);
            using var client = await WaitUntilReadyAsync(host, cancellationToken);
            await host.RequestStopAsync(cancellationToken);
            Assert.Equal(HostExitCodes.Success, await host.WaitForExitAsync(ExitTimeout, cancellationToken));
            output.WriteLine(host.Output);

            var match = NodeIdLine().Match(host.Output);
            Assert.True(match.Success, "启动日志中没有网关身份。");
            nodeIds.Add(match.Groups["nodeId"].Value);
        }

        // 两次启动使用同一套数据：身份在重启后保持，不因工作目录不同而新建（AT-10 中 NodeId 保留的部分）。
        Assert.Equal(nodeIds[0], nodeIds[1]);
        Assert.True(File.Exists(Path.Combine(dataDirectory.Path, DataDirectory.LockFileName)));
        Assert.True(File.Exists(Path.Combine(dataDirectory.Path, ConfigDatabase.FileName)));
        Assert.Empty(Directory.EnumerateFileSystemEntries(firstWorkingDirectory.Path));
        Assert.Empty(Directory.EnumerateFileSystemEntries(secondWorkingDirectory.Path));
    }

    [Theory]
    [Trait("Category", "Process")]
    [InlineData(InvalidDataDirectory.NotConfigured, "未配置数据目录")]
    [InlineData(InvalidDataDirectory.Relative, "必须是绝对路径")]
    [InlineData(InvalidDataDirectory.Missing, "不存在")]
    [InlineData(InvalidDataDirectory.File, "是一个文件")]
    public async Task InvalidDataDirectoryIsReportedWithConfigurationExitCode(InvalidDataDirectory kind, string expectedReason)
    {
        SkipUnlessLinux();
        var cancellationToken = TestContext.Current.CancellationToken;
        using var workingDirectory = TemporaryDirectory.Create();
        using var parent = TemporaryDirectory.Create();
        var configured = kind switch
        {
            InvalidDataDirectory.NotConfigured => null,
            InvalidDataDirectory.Relative => "data",
            InvalidDataDirectory.Missing => Path.Combine(parent.Path, "missing"),
            InvalidDataDirectory.File => CreateFile(parent.Path),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        await using var host = HostProcess.Start(workingDirectory.Path, Environments.Production, configured);
        try
        {
            Assert.Equal(HostExitCodes.ConfigurationError, await host.WaitForExitAsync(ExitTimeout, cancellationToken));
            Assert.Contains(expectedReason, host.Output, StringComparison.Ordinal);
            Assert.DoesNotContain("Now listening on", host.Output, StringComparison.Ordinal);
            Assert.Empty(Directory.EnumerateFileSystemEntries(workingDirectory.Path));
        }
        finally
        {
            output.WriteLine(host.Output);
        }
    }

    [Fact]
    [Trait("Category", "Process")]
    public async Task DisabledFileLockingIsRejectedBecauseTheLockWouldBeIneffective()
    {
        SkipUnlessLinux();
        var cancellationToken = TestContext.Current.CancellationToken;
        using var workingDirectory = TemporaryDirectory.Create();
        using var dataDirectory = TemporaryDirectory.Create();

        await using var host = HostProcess.Start(
            workingDirectory.Path,
            Environments.Production,
            dataDirectory.Path,
            environmentVariables: new Dictionary<string, string> { ["DOTNET_SYSTEM_IO_DISABLEFILELOCKING"] = "1" });
        try
        {
            Assert.Equal(HostExitCodes.ConfigurationError, await host.WaitForExitAsync(ExitTimeout, cancellationToken));
            Assert.Contains("DOTNET_SYSTEM_IO_DISABLEFILELOCKING", host.Output, StringComparison.Ordinal);
        }
        finally
        {
            output.WriteLine(host.Output);
        }
    }

    public enum InvalidDataDirectory
    {
        NotConfigured,
        Relative,
        Missing,
        File,
    }

    private static void SkipUnlessLinux() =>
        Assert.SkipUnless(OperatingSystem.IsLinux(), "真实进程测试依赖 SIGTERM 与 flock，只在 Linux 上执行；首版发布目标为 Linux x64，CI 在 Linux 上执行。");

    private static async Task<HttpClient> WaitUntilReadyAsync(HostProcess host, CancellationToken cancellationToken)
    {
        var address = await host.WaitForListeningAddressAsync(StartupTimeout, cancellationToken);
        var client = new HttpClient { BaseAddress = address, Timeout = TimeSpan.FromSeconds(10) };
        try
        {
            await host.WaitUntilReadyAsync(client, StartupTimeout, cancellationToken);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    [GeneratedRegex(@"网关身份：NodeId (?<nodeId>[0-9a-f-]{36})")]
    private static partial Regex NodeIdLine();

    private static string CreateFile(string directory)
    {
        var path = Path.Combine(directory, "not-a-directory");
        File.WriteAllText(path, string.Empty);
        return path;
    }
}
