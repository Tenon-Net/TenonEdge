using TenonEdge.Runtime.Deliveries;
using TenonEdge.Runtime.Diagnostics;

namespace TenonEdge.Host.Features.Runtime;

/// <summary>不依赖网页访问采样，不读取队列正文或定时查询 SQLite。</summary>
internal sealed class RuntimeTrendMonitor(DeliveryCache cache, RuntimeTrendHistory history, TimeProvider time) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(RuntimeTrendHistory.Interval, time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            { history.Sample(cache.GetActivityTotals()); }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
