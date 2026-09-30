using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using TenonEdge.Host.Bootstrap;
using TenonEdge.Host.Storage;
using TenonEdge.IntegrationTests.Infrastructure;

namespace TenonEdge.IntegrationTests.Storage;

/// <summary>
/// 数据目录部署参数的检查（总设计 §3.1，T0-04）：只接受绝对路径；路径错误明确报告，不悄悄新建另一套数据。
/// </summary>
public sealed class DataDirectoryTests
{
    [Fact]
    public void MissingSettingIsReportedAsConfigurationError()
    {
        var exception = Assert.Throws<HostStartupException>(() => Resolve(null, Environments.Production));

        Assert.Equal(HostExitCodes.ConfigurationError, exception.ExitCode);
        Assert.Contains(DataDirectory.ConfigurationKey, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("data")]
    [InlineData("./data")]
    [InlineData("../data")]
    public void RelativePathIsRejectedBecauseItDependsOnWorkingDirectory(string configured)
    {
        var exception = Assert.Throws<HostStartupException>(() => Resolve(configured, Environments.Production));

        Assert.Equal(HostExitCodes.ConfigurationError, exception.ExitCode);
        Assert.Contains("绝对路径", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AbsolutePathIsNormalizedWithoutTrailingSeparator()
    {
        using var parent = TemporaryDirectory.Create();
        var target = Directory.CreateDirectory(Path.Combine(parent.Path, "data")).FullName;
        var configured = Path.Combine(parent.Path, "other", "..", "data") + Path.DirectorySeparatorChar;

        var directory = Resolve(configured, Environments.Production);

        Assert.Equal(target, directory.Root);
        Assert.Equal(Path.Combine(target, DataDirectory.LockFileName), directory.LockFilePath);
    }

    [Fact]
    public void MissingDirectoryIsRejectedOutsideDevelopment()
    {
        using var parent = TemporaryDirectory.Create();
        var missing = Path.Combine(parent.Path, "missing");

        var exception = Assert.Throws<HostStartupException>(() => Resolve(missing, Environments.Production));

        Assert.Equal(HostExitCodes.ConfigurationError, exception.ExitCode);
        Assert.Contains("不存在", exception.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(missing));
    }

    [Fact]
    public void MissingDirectoryIsCreatedForOwnerOnlyInDevelopment()
    {
        using var parent = TemporaryDirectory.Create();
        var missing = Path.Combine(parent.Path, "dev-data");

        var directory = Resolve(missing, Environments.Development);

        Assert.True(Directory.Exists(directory.Root));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(PrivateFileSystem.DirectoryMode, File.GetUnixFileMode(directory.Root));
        }
    }

    [Fact]
    public void FileInsteadOfDirectoryIsRejected()
    {
        using var parent = TemporaryDirectory.Create();
        var file = Path.Combine(parent.Path, "not-a-directory");
        File.WriteAllText(file, string.Empty);

        var exception = Assert.Throws<HostStartupException>(() => Resolve(file, Environments.Development));

        Assert.Equal(HostExitCodes.ConfigurationError, exception.ExitCode);
        Assert.Contains("文件", exception.Message, StringComparison.Ordinal);
    }

    private static DataDirectory Resolve(string? configured, string environmentName)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [DataDirectory.ConfigurationKey] = configured })
            .Build();
        var environment = new HostingEnvironment { EnvironmentName = environmentName };
        return DataDirectory.Resolve(configuration, environment);
    }
}
