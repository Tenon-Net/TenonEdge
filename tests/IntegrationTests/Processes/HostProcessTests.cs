using System.Net;
using Microsoft.Extensions.Hosting;
using TenonEdge.IntegrationTests.Infrastructure;

namespace TenonEdge.IntegrationTests.Processes;

/// <summary>
/// 以真实进程启动已构建的宿主：真实端口监听、健康检查、默认保护与 SIGTERM 正常停机（V-01 最小宿主部分）。
/// 进程内 API 测试不能证明这些行为。
/// </summary>
public sealed class HostProcessTests(ITestOutputHelper output)
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    [Trait("Category", "Process")]
    public async Task BuiltHostServesHealthOnRealPortAndStopsGracefullyOnSigterm()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "发送 SIGTERM 只在 Linux 上实现；首版发布目标为 Linux x64，CI 在 Linux 上执行。");

        var cancellationToken = TestContext.Current.CancellationToken;
        using var workingDirectory = TemporaryDirectory.Create();
        using var dataDirectory = TemporaryDirectory.Create();
        await using var host = HostProcess.Start(workingDirectory.Path, Environments.Production, dataDirectory.Path);
        try
        {
            var address = await host.WaitForListeningAddressAsync(StartupTimeout, cancellationToken);
            Assert.Equal("127.0.0.1", address.Host);
            Assert.NotEqual(0, address.Port);

            using var client = new HttpClient { BaseAddress = address, Timeout = TimeSpan.FromSeconds(10) };
            await host.WaitUntilReadyAsync(client, StartupTimeout, cancellationToken);

            using var liveness = await client.GetAsync(new Uri("/healthz", UriKind.Relative), cancellationToken);
            Assert.Equal(HttpStatusCode.OK, liveness.StatusCode);
            Assert.Equal("Healthy", await liveness.Content.ReadAsStringAsync(cancellationToken));

            using var readiness = await client.GetAsync(new Uri("/readyz", UriKind.Relative), cancellationToken);
            Assert.Equal(HttpStatusCode.OK, readiness.StatusCode);
            Assert.Equal("Healthy", await readiness.Content.ReadAsStringAsync(cancellationToken));

            using var apiDocument = await client.GetAsync(
                new Uri("/api/edge-admin/v1/openapi/v1.json", UriKind.Relative),
                cancellationToken);
            Assert.Equal(HttpStatusCode.Unauthorized, apiDocument.StatusCode);

            await host.RequestStopAsync(cancellationToken);
            Assert.Equal(0, await host.WaitForExitAsync(ShutdownTimeout, cancellationToken));

            // 宿主不在进程工作目录中生成任何文件。
            Assert.Empty(Directory.EnumerateFileSystemEntries(workingDirectory.Path));
        }
        finally
        {
            output.WriteLine(host.Output);
        }
    }
}
