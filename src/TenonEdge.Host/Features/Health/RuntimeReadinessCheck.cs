using Microsoft.Extensions.Diagnostics.HealthChecks;
using TenonEdge.Host.Features.Runtime;

namespace TenonEdge.Host.Features.Health;

/// <summary>
/// 运行时就绪条件（总设计 §10.1）：运行时已启动——有效版本已激活，或尚无有效配置（也视为就绪）。
/// 处于“运行失败”、启动条件不满足、尚在启动判定或已开始停止时不就绪。原因只写在检查结果的说明中，<c>/readyz</c> 本身只返回状态。
/// </summary>
internal sealed class RuntimeReadinessCheck(RuntimeCoordinator coordinator) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var status = coordinator.Status;
        return Task.FromResult(status.State is RuntimeCoordinatorState.Active or RuntimeCoordinatorState.Idle
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy($"运行时状态为 {status.State}：{status.Reason}"));
    }
}
