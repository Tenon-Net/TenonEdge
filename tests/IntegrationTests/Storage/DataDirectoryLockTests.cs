using System.Runtime.Versioning;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using TenonEdge.Host.Bootstrap;
using TenonEdge.Host.Storage;
using TenonEdge.IntegrationTests.Infrastructure;

namespace TenonEdge.IntegrationTests.Storage;

/// <summary>
/// 单实例锁在同一进程内的行为（AT-10 单实例子项）。跨进程互斥、进程被强制终止后的释放由 DataDirectoryProcessTests 用真实进程验证。
/// </summary>
public sealed class DataDirectoryLockTests
{
    [Fact]
    public void SecondAcquisitionIsRejectedUntilTheFirstIsReleased()
    {
        using var root = TemporaryDirectory.Create();
        var directory = Resolve(root.Path);

        var first = DataDirectoryLock.Acquire(directory);
        var exception = Assert.Throws<HostStartupException>(() => DataDirectoryLock.Acquire(directory));
        Assert.Equal(HostExitCodes.DataDirectoryInUse, exception.ExitCode);
        Assert.Contains(directory.LockFilePath, exception.Message, StringComparison.Ordinal);

        first.Dispose();
        using var second = DataDirectoryLock.Acquire(directory);
        Assert.Same(directory, second.Directory);
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public void LockFileIsReadableAndWritableByOwnerOnly()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows 只用于开发调试，文件权限沿用目录继承的访问控制。");

        using var root = TemporaryDirectory.Create();
        var directory = Resolve(root.Path);

        using var dataDirectoryLock = DataDirectoryLock.Acquire(directory);

        Assert.Equal(PrivateFileSystem.FileMode, File.GetUnixFileMode(directory.LockFilePath));
    }

    private static DataDirectory Resolve(string path)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [DataDirectory.ConfigurationKey] = path })
            .Build();
        return DataDirectory.Resolve(configuration, new HostingEnvironment { EnvironmentName = Environments.Production });
    }
}
