using TenonEdge.Abstractions.Contracts;

namespace TenonEdge.Host.Features.SystemInfo;

/// <summary>即使没有输入或管理请求，也周期读取共用的校时状态；生命周期由宿主管理。</summary>
internal sealed class ClockSyncMonitor(IClockSyncStatus clock, TimeProvider time) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10), time);
        try
        {
            do
            {
                stoppingToken.ThrowIfCancellationRequested();
                _ = clock.State;
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 正常停机；不留下定时器或未观察的后台任务。
        }
    }
}
