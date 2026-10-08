using System.Globalization;
using System.Net;
using Microsoft.Extensions.Hosting;
using TenonEdge.Host.Bootstrap;
using TenonEdge.Host.Features.Audit;
using TenonEdge.Host.Features.Health;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Storage;
using TenonEdge.IntegrationTests.Infrastructure;

namespace TenonEdge.IntegrationTests.Processes;

/// <summary>
/// 启动保护的真实进程验证（T1-01，FS-05 的 T1 部分，AT-13 运行子集；总设计 §7.4、§8.4，D-07）：
/// 进程在运行有效版本时被强制终止（SIGKILL，与崩溃、断电一样没有机会执行停机逻辑），重启后计入非正常退出；
/// 从未稳定的版本累计 3 次后，上一版本曾稳定则自动回滚一次，否则或回滚生成的版本再次触发时进入“运行失败”：
/// 管理端口照常可用，<c>/readyz</c> 为 503，不形成崩溃循环。正常停止（SIGTERM）与已稳定版本上的退出不计入。
/// 让进程退出的是测试本身（强制终止），产品中没有专门令进程退出的测试单元。
/// </summary>
public sealed class StartupProtectionProcessTests(ITestOutputHelper output)
{
    private const string MeterA = """{"kind":"input.simulator","key":"meter-001/simulated","configuration":{"periodMs":1000,"seed":42}}""";
    private const string MeterB = """{"kind":"input.simulator","key":"meter-001/simulated","configuration":{"periodMs":2000,"seed":42}}""";

    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    [Trait("Category", "Process")]
    public async Task NormalStopsAreNotCountedButCrashesOfANeverStableVersionLeadToRunFailure()
    {
        SkipUnlessLinux();
        var cancellationToken = TestContext.Current.CancellationToken;
        using var work = TemporaryDirectory.Create();
        using var data = TemporaryDirectory.Create();
        var seed = WriteSeed(work.Path, MeterA);
        var state = new RuntimeStateReader(data.Path);

        // 正常停止两次：每次都清除“运行中”标记，退出码 0，不计数。
        for (var restart = 0; restart < 2; restart++)
        {
            await RunUntilReadyAsync(work.Path, data.Path, seed, stableAfterSeconds: 600, stop: Stop.Graceful, cancellationToken);
            Assert.Null(state.Pointers().Running);
        }

        Assert.Equal(0, state.Snapshot(1).AbnormalExitCount);

        // 强制终止三次：运行中标记留在库中，每次重启计入一次。
        for (var crash = 1; crash <= 3; crash++)
        {
            await RunUntilReadyAsync(work.Path, data.Path, seed, stableAfterSeconds: 600, stop: Stop.Kill, cancellationToken);
            Assert.Equal(1, state.Pointers().Running);
            Assert.Equal(crash - 1, state.Snapshot(1).AbnormalExitCount);
        }

        // 第四次启动：计入第 3 次，没有上一生效版本，进入“运行失败”。进程不退出，管理端口可用，就绪为 503。
        await using (var host = Start(work.Path, data.Path, seed, stableAfterSeconds: 600))
        {
            try
            {
                using var client = await ConnectAsync(host, cancellationToken);
                await host.WaitForApplicationStartedAsync(StartupTimeout, cancellationToken);
                Assert.Equal(HttpStatusCode.ServiceUnavailable, await GetStatusAsync(client, HealthEndpoints.ReadinessPath, cancellationToken));
                Assert.Equal(HttpStatusCode.OK, await GetStatusAsync(client, HealthEndpoints.LivenessPath, cancellationToken));
                Assert.Contains("运行失败，输入不启动", host.Output, StringComparison.Ordinal);
                Assert.Equal(3, state.Snapshot(1).AbnormalExitCount);
                Assert.Null(state.Pointers().Running);
                await host.RequestStopAsync(cancellationToken);
                Assert.Equal(HostExitCodes.Success, await host.WaitForExitAsync(ExitTimeout, cancellationToken));
            }
            finally
            {
                output.WriteLine(host.Output);
            }
        }

        Assert.Single(state.Events(RuntimeEventTypes.RunFailed));

        // 换一份种子（T3 之前相当于人工应用新配置）：新版本从零计数，重新激活。
        WriteSeed(work.Path, MeterB);
        await RunUntilReadyAsync(work.Path, data.Path, seed, stableAfterSeconds: 600, stop: Stop.Graceful, cancellationToken);
        Assert.Equal((2L, 0), (state.Pointers().Effective!.Value, state.Snapshot(2).AbnormalExitCount));
    }

    [Fact]
    [Trait("Category", "Process")]
    public async Task CrashesOfANewVersionRollBackOnceToTheLastStableVersion()
    {
        SkipUnlessLinux();
        var cancellationToken = TestContext.Current.CancellationToken;
        using var work = TemporaryDirectory.Create();
        using var data = TemporaryDirectory.Create();
        var seed = WriteSeed(work.Path, MeterA);
        var state = new RuntimeStateReader(data.Path);

        // r1 连续运行满稳定时间（1 秒）后被强制终止：已稳定的版本上的退出不计入。
        await using (var stable = Start(work.Path, data.Path, seed, stableAfterSeconds: 1))
        {
            try
            {
                using var client = await ConnectAsync(stable, cancellationToken);
                await stable.WaitUntilReadyAsync(client, StartupTimeout, cancellationToken);
                await state.WaitUntilAsync(reader => reader.Snapshot(1).IsStable, StartupTimeout, cancellationToken);
                await stable.KillAsync(cancellationToken);
            }
            finally
            {
                output.WriteLine(stable.Output);
            }
        }

        // 新种子生成 r2；它从未稳定，被强制终止三次。r1 那次退出在 r2 生效前已处理，不计入。
        WriteSeed(work.Path, MeterB);
        for (var crash = 1; crash <= 3; crash++)
        {
            await RunUntilReadyAsync(work.Path, data.Path, seed, stableAfterSeconds: 600, stop: Stop.Kill, cancellationToken);
        }

        Assert.Equal(0, state.Snapshot(1).AbnormalExitCount);
        Assert.Equal(2, state.Snapshot(2).AbnormalExitCount);

        // 第四次启动：r2 计入第 3 次，r1 曾经稳定，自动回滚到 r1 的内容（新版本 r3，操作人为“系统”）并照常就绪。
        await RunUntilReadyAsync(work.Path, data.Path, seed, stableAfterSeconds: 600, stop: Stop.Kill, cancellationToken);
        var rollback = state.Snapshot(3);
        Assert.Equal((RuntimeSnapshotOrigins.AutoRollback, "r1", RuntimeVersionStore.SystemOperator), (rollback.Origin, rollback.OriginDetail, rollback.CreatedBy));
        Assert.Equal(state.Snapshot(1).Content, rollback.Content);
        Assert.Equal((3L, 2L), (state.Pointers().Effective!.Value, state.Pointers().Previous!.Value));
        Assert.Single(state.Audit(AuditActions.AutoRollback));

        // 回滚生成的 r3 同样从未稳定：再强制终止两次（加上面一次共 3 次），之后进入“运行失败”，不再回滚。
        await RunUntilReadyAsync(work.Path, data.Path, seed, stableAfterSeconds: 600, stop: Stop.Kill, cancellationToken);
        await RunUntilReadyAsync(work.Path, data.Path, seed, stableAfterSeconds: 600, stop: Stop.Kill, cancellationToken);
        await using (var failed = Start(work.Path, data.Path, seed, stableAfterSeconds: 600))
        {
            try
            {
                using var client = await ConnectAsync(failed, cancellationToken);
                await failed.WaitForApplicationStartedAsync(StartupTimeout, cancellationToken);
                Assert.Equal(HttpStatusCode.ServiceUnavailable, await GetStatusAsync(client, HealthEndpoints.ReadinessPath, cancellationToken));
                Assert.Contains("自动回滚生成的版本", failed.Output, StringComparison.Ordinal);
                await failed.RequestStopAsync(cancellationToken);
                Assert.Equal(HostExitCodes.Success, await failed.WaitForExitAsync(ExitTimeout, cancellationToken));
            }
            finally
            {
                output.WriteLine(failed.Output);
            }
        }

        Assert.Equal(3, state.Snapshot(3).AbnormalExitCount);
        Assert.Equal(3, state.Snapshots().Count);
    }

    [Theory]
    [Trait("Category", "Process")]
    [InlineData("--TenonEdge:Runtime:SeedSnapshot", "seed.json")]
    [InlineData("--TenonEdge:Runtime:StableAfterSeconds", "0")]
    public async Task InvalidRuntimeDeploymentParametersStopTheHostWithTheConfigurationExitCode(string name, string value)
    {
        // 运行时的部署参数与其他部署参数一样在启动检查中校验：以退出码 2 结束并说明原因，不崩溃，也不在数据目录中留下任何文件。
        SkipUnlessLinux();
        var cancellationToken = TestContext.Current.CancellationToken;
        using var work = TemporaryDirectory.Create();
        using var data = TemporaryDirectory.Create();

        await using var host = HostProcess.Start(work.Path, Environments.Production, data.Path, [name, value]);
        try
        {
            Assert.Equal(HostExitCodes.ConfigurationError, await host.WaitForExitAsync(ExitTimeout, cancellationToken));
            Assert.Contains(name.TrimStart('-'), host.Output, StringComparison.Ordinal);
            Assert.DoesNotContain("Unhandled exception", host.Output, StringComparison.Ordinal);
            Assert.Empty(Directory.EnumerateFileSystemEntries(data.Path));
        }
        finally
        {
            output.WriteLine(host.Output);
        }
    }

    private enum Stop
    {
        Graceful,
        Kill,
    }

    private static void SkipUnlessLinux() =>
        Assert.SkipUnless(OperatingSystem.IsLinux(), "真实进程测试依赖 SIGTERM、SIGKILL 与 flock，只在 Linux 上执行；首版发布目标为 Linux x64，CI 在 Linux 上执行。");

    private static string WriteSeed(string directory, string unit)
    {
        var path = Path.Combine(directory, "seed.json");
        File.WriteAllText(path, $$"""{"schemaVersion":1,"units":[{{unit}}]}""");
        return path;
    }

    private static HostProcess Start(string work, string data, string seed, int stableAfterSeconds) => HostProcess.Start(
        work,
        Environments.Production,
        data,
        [
            $"--{RuntimeSettings.SeedSnapshotKey}", seed,
            $"--{RuntimeSettings.StableAfterSecondsKey}", stableAfterSeconds.ToString(CultureInfo.InvariantCulture),
        ]);

    /// <summary>启动宿主并等到就绪（有效版本已激活），然后按要求正常停止或强制终止。</summary>
    private async Task RunUntilReadyAsync(string work, string data, string seed, int stableAfterSeconds, Stop stop, CancellationToken cancellationToken)
    {
        await using var host = Start(work, data, seed, stableAfterSeconds);
        try
        {
            using var client = await ConnectAsync(host, cancellationToken);
            await host.WaitUntilReadyAsync(client, StartupTimeout, cancellationToken);
            if (stop == Stop.Kill)
            {
                await host.KillAsync(cancellationToken);
            }
            else
            {
                await host.RequestStopAsync(cancellationToken);
                Assert.Equal(HostExitCodes.Success, await host.WaitForExitAsync(ExitTimeout, cancellationToken));
            }
        }
        finally
        {
            output.WriteLine(host.Output);
        }
    }

    private static async Task<HttpClient> ConnectAsync(HostProcess host, CancellationToken cancellationToken)
    {
        var address = await host.WaitForListeningAddressAsync(StartupTimeout, cancellationToken);
        return new HttpClient { BaseAddress = address, Timeout = TimeSpan.FromSeconds(10) };
    }

    private static async Task<HttpStatusCode> GetStatusAsync(HttpClient client, string path, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(new Uri(path, UriKind.Relative), cancellationToken);
        return response.StatusCode;
    }
}
