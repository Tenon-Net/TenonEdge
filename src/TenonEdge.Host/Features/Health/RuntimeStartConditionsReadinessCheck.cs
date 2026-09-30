using Microsoft.Extensions.Diagnostics.HealthChecks;
using TenonEdge.Host.Bootstrap;

namespace TenonEdge.Host.Features.Health;

/// <summary>
/// 就绪条件：没有阻止运行时启动的原因（<see cref="RuntimeStartConditions"/>）。
/// 原因只写在检查结果的说明中，供日志与系统信息使用，<c>/readyz</c> 本身只返回状态。
/// </summary>
internal sealed class RuntimeStartConditionsReadinessCheck(RuntimeStartConditions conditions) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (conditions.AllowsRuntimeStart)
        {
            return Task.FromResult(HealthCheckResult.Healthy());
        }

        var codes = string.Join(", ", conditions.Blockers.Select(blocker => blocker.Code));
        return Task.FromResult(HealthCheckResult.Unhealthy($"运行时启动条件不满足：{codes}"));
    }
}
