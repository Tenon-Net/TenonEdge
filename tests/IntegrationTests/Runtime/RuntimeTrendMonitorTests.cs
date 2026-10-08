using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.Runtime.Deliveries;
using TenonEdge.Runtime.Diagnostics;

namespace TenonEdge.IntegrationTests.Runtime;

public sealed class RuntimeTrendMonitorTests
{
    [Fact]
    public async Task RealHostSamplesWithoutRequestsAndStopsItsSingleMonitor()
    {
        var factory = new TenonEdgeApplicationFactory();
        RuntimeTrendMonitor monitor;
        try
        {
            var services = factory.Services;
            monitor = Assert.Single(services.GetServices<IHostedService>().OfType<RuntimeTrendMonitor>());
            var history = services.GetRequiredService<RuntimeTrendHistory>();
            var cache = services.GetRequiredService<DeliveryCache>();
            Assert.Empty(history.Snapshot());
            cache.PendingCounters.RecordActivity(RuntimeActivity.Received, 3);
            cache.PendingCounters.RecordActivity(RuntimeActivity.Sent, 2);
            cache.PendingCounters.RecordActivity(RuntimeActivity.SendFailure);
            await OutputPipelineTests.UntilAsync(() => history.Snapshot().Count > 0);
            var point = Assert.Single(history.Snapshot());
            Assert.Equal(3, point.Received);
            Assert.Equal(2, point.Sent);
            Assert.Equal(1, point.SendFailures);
            Assert.True(point.DurationSeconds >= 10);
        }
        finally { await factory.DisposeAsync(); }
        await monitor.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(monitor.ExecuteTask.IsCompletedSuccessfully);
    }
}
