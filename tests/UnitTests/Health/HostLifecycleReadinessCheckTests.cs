using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using TenonEdge.Host.Features.Health;

namespace TenonEdge.UnitTests.Health;

/// <summary>
/// 宿主生命周期就绪条件：未启动完成与已开始停机两种状态在进程内 API 测试中无法稳定观察，在此直接验证。
/// </summary>
public sealed class HostLifecycleReadinessCheckTests
{
    [Fact]
    public async Task NotReadyBeforeApplicationStarted()
    {
        using var lifetime = new ControllableLifetime();

        var result = await CheckAsync(lifetime);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    [Fact]
    public async Task ReadyAfterApplicationStarted()
    {
        using var lifetime = new ControllableLifetime();
        await lifetime.StartedSource.CancelAsync();

        var result = await CheckAsync(lifetime);

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task NotReadyOnceStoppingBegins()
    {
        using var lifetime = new ControllableLifetime();
        await lifetime.StartedSource.CancelAsync();
        lifetime.StopApplication();

        var result = await CheckAsync(lifetime);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    private static Task<HealthCheckResult> CheckAsync(IHostApplicationLifetime lifetime) =>
        new HostLifecycleReadinessCheck(lifetime).CheckHealthAsync(
            new HealthCheckContext(),
            TestContext.Current.CancellationToken);

    private sealed class ControllableLifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource _stopping = new();
        private readonly CancellationTokenSource _stopped = new();

        public CancellationTokenSource StartedSource { get; } = new();

        public CancellationToken ApplicationStarted => StartedSource.Token;

        public CancellationToken ApplicationStopping => _stopping.Token;

        public CancellationToken ApplicationStopped => _stopped.Token;

        public void StopApplication() => _stopping.Cancel();

        public void Dispose()
        {
            StartedSource.Dispose();
            _stopping.Dispose();
            _stopped.Dispose();
        }
    }
}
