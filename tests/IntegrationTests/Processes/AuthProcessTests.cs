using System.Net;
using System.Text;
using Microsoft.Extensions.Hosting;
using TenonEdge.Host.Bootstrap;
using TenonEdge.Host.Features.Audit;
using TenonEdge.Host.Storage;
using TenonEdge.IntegrationTests.Infrastructure;

namespace TenonEdge.IntegrationTests.Processes;

/// <summary>
/// 首次设置与登录的真实进程验证（T0-07）：设置窗口开放时输出一行不含秘密的提示；首次设置记录真实的来源地址；
/// 服务输出中不出现密码；超过 1 MiB 的请求体被拒绝（总设计 §8.7）。
/// </summary>
public sealed class AuthProcessTests(ITestOutputHelper output)
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    [Trait("Category", "Process")]
    public async Task SetupWindowHintIsLoggedAndPasswordsNeverAppearInTheOutput()
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
            Assert.Contains("首次设置窗口已开放", host.Output, StringComparison.Ordinal);

            using (var setup = await client.CompleteSetupAsync(cancellationToken))
            {
                Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
            }

            using (var wrong = await client.LoginAsync("admin", "Wrong-Password-1", cancellationToken))
            {
                Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
            }

            await host.RequestStopAsync(cancellationToken);
            Assert.Equal(HostExitCodes.Success, await host.WaitForExitAsync(ExitTimeout, cancellationToken));

            Assert.DoesNotContain(ApiRequests.StrongPassword, host.Output, StringComparison.Ordinal);
            Assert.DoesNotContain("Wrong-Password-1", host.Output, StringComparison.Ordinal);
        }
        finally
        {
            output.WriteLine(host.Output);
        }

        using var store = TestConfigStore.Open(dataDirectory.Path);
        store.Initialize();
        var state = store.Database.Client.Queryable<SystemStateRecord>().InSingle(SystemStateRecord.SingletonId);
        Assert.Equal("127.0.0.1", state.SetupSourceIp);
        var setupAudit = Assert.Single(store.Database.Client.Queryable<AuditLogRecord>().Where(record => record.Action == AuditActions.SetupCompleted).ToList());
        Assert.Equal("127.0.0.1", setupAudit.SourceIp);
    }

    [Fact]
    [Trait("Category", "Process")]
    public async Task RequestBodyLargerThanOneMebibyteIsRejected()
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
            var oversized = $$"""{"accountName":"admin","password":"{{new string('x', (int)KestrelListeners.MaxRequestBodySize)}}"}""";

            using var content = new StringContent(oversized, Encoding.UTF8, "application/json");
            using var response = await client.PostAsync(new Uri(ApiRequests.Login, UriKind.Relative), content, cancellationToken);

            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
            await host.RequestStopAsync(cancellationToken);
            Assert.Equal(HostExitCodes.Success, await host.WaitForExitAsync(ExitTimeout, cancellationToken));
        }
        finally
        {
            output.WriteLine(host.Output);
        }
    }

    private static void SkipUnlessLinux() =>
        Assert.SkipUnless(OperatingSystem.IsLinux(), "真实进程测试依赖 SIGTERM 与 flock，只在 Linux 上执行；首版发布目标为 Linux x64，CI 在 Linux 上执行。");
}
