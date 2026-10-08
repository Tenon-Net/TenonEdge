using Microsoft.Extensions.Hosting;
using TenonEdge.Host.Bootstrap;

namespace TenonEdge.UnitTests.Bootstrap;

/// <summary>
/// 宿主停止后的退出码（T1-01，§6 决定 45）：后台服务因未处理的异常结束时为 6，使服务管理器把它当作失败并重启；否则为 0。
/// </summary>
public sealed class HostExitCodesTests
{
    [Fact]
    public async Task AFaultedBackgroundServiceGivesANonZeroExitCode()
    {
        using var faulted = new Worker(_ => throw new InvalidOperationException("模拟的未处理异常"));
        using var normal = new Worker(token => Task.Delay(Timeout.Infinite, token));
        await faulted.StartAsync(CancellationToken.None);
        await normal.StartAsync(CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => faulted.ExecuteTask!);

        Assert.Equal(HostExitCodes.BackgroundServiceFailed, HostExitCodes.AfterShutdown([normal, faulted]));
        await normal.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ServicesThatStoppedNormallyGiveZero()
    {
        using var completed = new Worker(_ => Task.CompletedTask);
        using var stopped = new Worker(token => Task.Delay(Timeout.Infinite, token));
        await completed.StartAsync(CancellationToken.None);
        await stopped.StartAsync(CancellationToken.None);
        await stopped.StopAsync(CancellationToken.None);

        Assert.Equal(HostExitCodes.Success, HostExitCodes.AfterShutdown([completed, stopped]));
        Assert.Equal(HostExitCodes.Success, HostExitCodes.AfterShutdown([]));
    }

    private sealed class Worker(Func<CancellationToken, Task> execute) : BackgroundService
    {
        protected override Task ExecuteAsync(CancellationToken stoppingToken) => execute(stoppingToken);
    }
}
