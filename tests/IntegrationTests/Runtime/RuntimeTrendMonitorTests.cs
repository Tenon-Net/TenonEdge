using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.Runtime.Deliveries;
using TenonEdge.Runtime.Diagnostics;

namespace TenonEdge.IntegrationTests.Runtime;

public sealed class RuntimeTrendMonitorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealHostSamplesWithoutRequestsAndStopsItsSingleMonitor(bool sampledBeforeObservation)
    {
        using var data = TemporaryDirectory.Create();
        var clock = new MonitorClock();
        var factory = new FixedDataDirectoryApplicationFactory(data.Path, configureServices: services =>
        {
            // 只替换本被测服务的时钟，不冻结队列/身份/停机等其他宿主服务的时钟。
            var registration = Assert.Single(services, item => item.ServiceType == typeof(IHostedService) && item.ImplementationType == typeof(RuntimeTrendMonitor));
            Assert.Equal(ServiceLifetime.Singleton, registration.Lifetime);
            services[services.IndexOf(registration)] = ServiceDescriptor.Singleton<IHostedService>(provider =>
                new RuntimeTrendMonitor(provider.GetRequiredService<DeliveryCache>(), provider.GetRequiredService<RuntimeTrendHistory>(), clock));
            services.Replace(ServiceDescriptor.Singleton(new RuntimeTrendHistory(clock)));
        });
        RuntimeTrendMonitor monitor;
        RuntimeTrendHistory history;
        var expectedPoints = sampledBeforeObservation ? 2 : 1;
        try
        {
            var services = factory.Services;
            monitor = Assert.Single(services.GetServices<IHostedService>().OfType<RuntimeTrendMonitor>());
            history = services.GetRequiredService<RuntimeTrendHistory>();
            var cache = services.GetRequiredService<DeliveryCache>();
            await clock.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            if (sampledBeforeObservation)
            {
                clock.Advance(RuntimeTrendHistory.Interval);
                await OutputPipelineTests.UntilAsync(() => history.Snapshot().Count == 1);
                var initial = Assert.Single(history.Snapshot());
                Assert.Equal((0L, 0L, 0L), (initial.Received, initial.Sent, initial.SendFailures));
            }
            else
            { Assert.Empty(history.Snapshot()); }
            cache.PendingCounters.RecordActivity(RuntimeActivity.Received, 3);
            cache.PendingCounters.RecordActivity(RuntimeActivity.Sent, 2);
            cache.PendingCounters.RecordActivity(RuntimeActivity.SendFailure);
            clock.Advance(RuntimeTrendHistory.Interval - TimeSpan.FromTicks(1));
            Assert.Equal(expectedPoints - 1, history.Snapshot().Count);
            clock.Advance(TimeSpan.FromTicks(1));
            await OutputPipelineTests.UntilAsync(() => history.Snapshot().Count == expectedPoints);
            var points = history.Snapshot();
            Assert.Equal(expectedPoints, points.Count);
            var point = points[^1];
            Assert.Equal(3, point.Received);
            Assert.Equal(2, point.Sent);
            Assert.Equal(1, point.SendFailures);
            Assert.Equal(10, point.DurationSeconds);
        }
        finally { await factory.DisposeAsync(); }
        await monitor.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(monitor.ExecuteTask.IsCompletedSuccessfully);
        clock.Advance(RuntimeTrendHistory.Interval);
        Assert.Equal(expectedPoints, history.Snapshot().Count);
    }

    // 只控制时间；仍由产品原BackgroundService创建PeriodicTimer并调用原Sample，没有直接向历史写测试数据。
    private sealed class MonitorClock : TimeProvider
    {
        private readonly FakeTimeProvider _inner = new(DateTimeOffset.UtcNow);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override long TimestampFrequency => _inner.TimestampFrequency;
        public override long GetTimestamp() => _inner.GetTimestamp();
        public override DateTimeOffset GetUtcNow() => _inner.GetUtcNow();
        public void Advance(TimeSpan amount) => _inner.Advance(amount);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = _inner.CreateTimer(callback, state, dueTime, period);
            if (dueTime == RuntimeTrendHistory.Interval && period == RuntimeTrendHistory.Interval)
            { Started.TrySetResult(); }
            return timer;
        }
    }
}
