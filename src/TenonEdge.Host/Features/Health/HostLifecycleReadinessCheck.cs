using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace TenonEdge.Host.Features.Health;

/// <summary>
/// 宿主生命周期就绪条件：全部托管服务已启动（ApplicationStarted）且尚未开始停机（ApplicationStopping）。
/// 它只反映宿主本身；运行时状态等其他就绪条件由相应组件另行注册。
/// </summary>
internal sealed class HostLifecycleReadinessCheck(IHostApplicationLifetime lifetime) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var ready = lifetime.ApplicationStarted.IsCancellationRequested
            && !lifetime.ApplicationStopping.IsCancellationRequested;

        return Task.FromResult(ready ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy());
    }
}
