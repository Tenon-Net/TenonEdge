using System.Net;
using Microsoft.Extensions.Hosting;
using TenonEdge.Host.Bootstrap;
using TenonEdge.IntegrationTests.Infrastructure;

namespace TenonEdge.IntegrationTests.Processes;

/// <summary>
/// 容器数据卷检查在真实进程中的接线（T0-06，V-08）：以官方 .NET 镜像设置的 DOTNET_RUNNING_IN_CONTAINER=true 模拟容器环境，
/// 检查宿主按挂载表判断数据目录，并据此决定就绪状态与日志。挂载表的解析逻辑由单元测试覆盖，预期结果由测试按同一逻辑独立推算，
/// 因此在临时目录位于 tmpfs 或根分区的机器上都成立。真实容器中挂卷与未挂卷两种情况另以容器验证，见 docs/implementation-status.md。
/// </summary>
public sealed class ContainerVolumeProcessTests(ITestOutputHelper output)
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    [Trait("Category", "Process")]
    public async Task ContainerEnvironmentAppliesTheDataVolumeConditionToReadiness()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "挂载表检查只在 Linux 上实现；首版发布目标为 Linux x64，CI 在 Linux 上执行。");
        var cancellationToken = TestContext.Current.CancellationToken;
        using var workingDirectory = TemporaryDirectory.Create();
        using var dataDirectory = TemporaryDirectory.Create();
        var mount = ContainerDataVolume.FindMount(
            ContainerDataVolume.ResolveSymbolicLinks(dataDirectory.Path),
            await File.ReadAllTextAsync("/proc/self/mountinfo", cancellationToken));
        var persistent = mount is not null && ContainerDataVolume.IsPersistentVolume(mount);
        output.WriteLine($"数据目录 {dataDirectory.Path} 位于 {mount?.MountPoint}（{mount?.FileSystemType}），预期持久：{persistent}");

        await using var host = HostProcess.Start(
            workingDirectory.Path,
            Environments.Production,
            dataDirectory.Path,
            environmentVariables: new Dictionary<string, string> { ["DOTNET_RUNNING_IN_CONTAINER"] = "true" });
        try
        {
            var address = await host.WaitForListeningAddressAsync(StartupTimeout, cancellationToken);
            await host.WaitForApplicationStartedAsync(StartupTimeout, cancellationToken);
            using var client = new HttpClient { BaseAddress = address, Timeout = TimeSpan.FromSeconds(10) };

            using var readiness = await client.GetAsync(new Uri("/readyz", UriKind.Relative), cancellationToken);
            using var liveness = await client.GetAsync(new Uri("/healthz", UriKind.Relative), cancellationToken);
            Assert.Equal(HttpStatusCode.OK, liveness.StatusCode);
            if (persistent)
            {
                Assert.Equal(HttpStatusCode.OK, readiness.StatusCode);
                Assert.Contains("在容器中运行：数据目录", host.Output, StringComparison.Ordinal);
            }
            else
            {
                // 运行时不启动，但管理界面仍可使用：存活检查照常，就绪检查返回 503，日志提示挂载数据卷。
                Assert.Equal(HttpStatusCode.ServiceUnavailable, readiness.StatusCode);
                Assert.Contains("不在已挂载的持久卷上", host.Output, StringComparison.Ordinal);
                Assert.Contains("请为数据目录挂载数据卷", host.Output, StringComparison.Ordinal);
            }

            await host.RequestStopAsync(cancellationToken);
            Assert.Equal(HostExitCodes.Success, await host.WaitForExitAsync(ExitTimeout, cancellationToken));
        }
        finally
        {
            output.WriteLine(host.Output);
        }
    }
}
