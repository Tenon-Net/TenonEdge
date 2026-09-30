using System.Net;
using Microsoft.Extensions.Hosting;
using TenonEdge.Host.Bootstrap;
using TenonEdge.Host.Features.Audit;
using TenonEdge.Host.Security;
using TenonEdge.Host.Storage;
using TenonEdge.IntegrationTests.Infrastructure;

namespace TenonEdge.IntegrationTests.Processes;

/// <summary>
/// 主机侧管理员重置（T0-07；功能需求 §1.5、§1.7）：命令与服务共用数据目录锁，服务运行时拒绝执行；
/// 停止服务后执行，清除指定管理员的密码并重新开放设置窗口，只能为该账号设置新密码，其他管理员不受影响；
/// 命令与服务的输出中都不出现密码；重置与重新设置都记入审计。
/// </summary>
public sealed class AdministratorResetProcessTests(ITestOutputHelper output)
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    [Trait("Category", "Process")]
    public async Task ResetIsRefusedWhileTheServiceIsRunning()
    {
        SkipUnlessLinux();
        var cancellationToken = TestContext.Current.CancellationToken;
        using var workingDirectory = TemporaryDirectory.Create();
        using var dataDirectory = TemporaryDirectory.Create();

        await using var host = HostProcess.Start(workingDirectory.Path, Environments.Production, dataDirectory.Path);
        try
        {
            using var client = await StartAsync(host, cancellationToken);
            using (var setup = await client.CompleteSetupAsync(cancellationToken))
            {
                Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
            }

            var (exitCode, commandOutput) = await RunResetAsync(workingDirectory.Path, dataDirectory.Path, cancellationToken);
            output.WriteLine(commandOutput);

            Assert.Equal(HostExitCodes.DataDirectoryInUse, exitCode);
            Assert.Contains("执行主机侧维护命令前须先停止服务", commandOutput, StringComparison.Ordinal);
            using var login = await client.LoginAsync("admin", ApiRequests.StrongPassword, cancellationToken);
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);

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
    public async Task ResetClearsThePasswordAndReopensSetupOnlyForThatAdministrator()
    {
        SkipUnlessLinux();
        var cancellationToken = TestContext.Current.CancellationToken;
        using var workingDirectory = TemporaryDirectory.Create();
        using var dataDirectory = TemporaryDirectory.Create();

        await using (var first = HostProcess.Start(workingDirectory.Path, Environments.Production, dataDirectory.Path))
        {
            using var client = await StartAsync(first, cancellationToken);
            using (var setup = await client.CompleteSetupAsync(cancellationToken))
            {
                Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
            }

            await StopAsync(first, cancellationToken);
        }

        // 另一名管理员：由测试夹具在服务停止时写入。
        using (var store = TestConfigStore.Open(dataDirectory.Path))
        {
            store.Initialize();
            var hashing = new PasswordHashing();
            store.Database.Client.Insertable(new UserAccountRecord
            {
                Id = 42,
                AccountName = "ops",
                NormalizedAccountName = "ops",
                PasswordHash = hashing.Hash(ApiRequests.AnotherStrongPassword),
                Role = nameof(UserRole.Administrator),
                SecurityStamp = SecurityStamps.New(),
                IsEnabled = true,
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            }).ExecuteCommand();
        }

        var (exitCode, commandOutput) = await RunResetAsync(workingDirectory.Path, dataDirectory.Path, cancellationToken);
        output.WriteLine(commandOutput);
        Assert.Equal(HostExitCodes.Success, exitCode);
        Assert.Contains("已清除管理员账号 admin 的密码", commandOutput, StringComparison.Ordinal);
        Assert.DoesNotContain(ApiRequests.StrongPassword, commandOutput, StringComparison.Ordinal);

        const string newPassword = "Reset-Password-2026";
        await using (var second = HostProcess.Start(workingDirectory.Path, Environments.Production, dataDirectory.Path))
        {
            try
            {
                using var client = await StartAsync(second, cancellationToken);
                using (var status = await client.GetAsync(new Uri(ApiRequests.Setup, UriKind.Relative), cancellationToken))
                {
                    var body = await status.ReadJsonAsync(cancellationToken);
                    Assert.Equal("resetPending", body.GetProperty("state").GetString());
                    Assert.Equal("admin", body.GetProperty("resetAccountName").GetString());
                    Assert.True(body.GetProperty("windowOpen").GetBoolean());
                }

                Assert.Contains("管理员账号 admin 已在主机上重置", second.Output, StringComparison.Ordinal);
                using (var oldPassword = await client.LoginAsync("admin", ApiRequests.StrongPassword, cancellationToken))
                {
                    Assert.Equal(HttpStatusCode.Unauthorized, oldPassword.StatusCode);
                }

                using (var otherAccount = await client.CompleteSetupAsync(cancellationToken, accountName: "someone", password: newPassword))
                {
                    Assert.Equal(HttpStatusCode.BadRequest, otherAccount.StatusCode);
                }

                using (var setup = await client.CompleteSetupAsync(cancellationToken, accountName: "admin", password: newPassword))
                {
                    Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
                }

                using var fresh = new HttpClient { BaseAddress = client.BaseAddress, Timeout = TimeSpan.FromSeconds(10) };
                using (var login = await fresh.LoginAsync("admin", newPassword, cancellationToken))
                {
                    Assert.Equal(HttpStatusCode.OK, login.StatusCode);
                }

                using (var unaffected = await fresh.LoginAsync("ops", ApiRequests.AnotherStrongPassword, cancellationToken))
                {
                    Assert.Equal(HttpStatusCode.OK, unaffected.StatusCode);
                }

                await StopAsync(second, cancellationToken);
                Assert.DoesNotContain(newPassword, second.Output, StringComparison.Ordinal);
                Assert.DoesNotContain(ApiRequests.StrongPassword, second.Output, StringComparison.Ordinal);
            }
            finally
            {
                output.WriteLine(second.Output);
            }
        }

        using var inspection = TestConfigStore.Open(dataDirectory.Path);
        inspection.Initialize();
        var audit = inspection.Database.Client.Queryable<AuditLogRecord>().OrderBy(record => record.Id).ToList();
        var reset = Assert.Single(audit, record => record.Action == AuditActions.AdministratorReset);
        Assert.Equal("admin", reset.Target);
        Assert.Contains("主机侧命令", reset.Detail, StringComparison.Ordinal);
        Assert.Equal(2, audit.Count(record => record.Action == AuditActions.SetupCompleted && record.Result == AuditResults.Success));
    }

    [Theory]
    [Trait("Category", "Process")]
    [InlineData(false, "admin", "尚未完成首次设置")]
    [InlineData(true, "nobody", "账号 nobody 不存在")]
    public async Task ResetThatCannotApplyEndsWithMaintenanceFailureCode(bool completeSetup, string accountName, string reason)
    {
        SkipUnlessLinux();
        var cancellationToken = TestContext.Current.CancellationToken;
        using var workingDirectory = TemporaryDirectory.Create();
        using var dataDirectory = TemporaryDirectory.Create();
        await using (var host = HostProcess.Start(workingDirectory.Path, Environments.Production, dataDirectory.Path))
        {
            using var client = await StartAsync(host, cancellationToken);
            if (completeSetup)
            {
                using var setup = await client.CompleteSetupAsync(cancellationToken);
                Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
            }

            await StopAsync(host, cancellationToken);
        }

        var (exitCode, commandOutput) = await RunResetAsync(workingDirectory.Path, dataDirectory.Path, cancellationToken, accountName);
        output.WriteLine(commandOutput);

        Assert.Equal(HostExitCodes.MaintenanceCommandFailed, exitCode);
        Assert.Contains(reason, commandOutput, StringComparison.Ordinal);
    }

    private static void SkipUnlessLinux() =>
        Assert.SkipUnless(OperatingSystem.IsLinux(), "真实进程测试依赖 SIGTERM 与 flock，只在 Linux 上执行；首版发布目标为 Linux x64，CI 在 Linux 上执行。");

    private static async Task<HttpClient> StartAsync(HostProcess host, CancellationToken cancellationToken)
    {
        var address = await host.WaitForListeningAddressAsync(StartupTimeout, cancellationToken);
        var client = new HttpClient { BaseAddress = address, Timeout = TimeSpan.FromSeconds(10) };
        await host.WaitUntilReadyAsync(client, StartupTimeout, cancellationToken);
        return client;
    }

    private static async Task StopAsync(HostProcess host, CancellationToken cancellationToken)
    {
        await host.RequestStopAsync(cancellationToken);
        Assert.Equal(HostExitCodes.Success, await host.WaitForExitAsync(ExitTimeout, cancellationToken));
    }

    private static Task<(int ExitCode, string Output)> RunResetAsync(
        string workingDirectory,
        string dataDirectory,
        CancellationToken cancellationToken,
        string? accountName = null)
    {
        List<string> arguments = [MaintenanceCommands.ResetAdministrator, $"--{DataDirectory.ConfigurationKey}", dataDirectory];
        if (accountName is not null)
        {
            arguments.AddRange(["--account", accountName]);
        }

        return HostProcess.RunCommandAsync(workingDirectory, arguments, ExitTimeout, cancellationToken);
    }
}
