using System.Threading.Channels;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Host.Features.SystemInfo;
using TenonEdge.Host.Storage;
using TenonEdge.IntegrationTests.Infrastructure;

namespace TenonEdge.IntegrationTests.SystemInfo;

public sealed class ClockSyncMonitorTests
{
    [Fact]
    public async Task HostStartsOneMonitorWithoutHttpRequestsAndStopsItOnDisposal()
    {
        var token = TestContext.Current.CancellationToken;
        var clock = new ObservedClock();
        await using var baseFactory = new TenonEdgeApplicationFactory();
        var factory = baseFactory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            // 保留生产 ClockSyncMonitor 注册，只隔离同样会主动读取时钟的清理服务。
            // 否则启动清理的一次读取会被误当成监控器的第二次周期采样。
            services.Remove(Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IHostedService)
                && descriptor.ImplementationType == typeof(DataRetentionService)));
            services.AddSingleton<IClockSyncStatus>(clock);
        }));
        ClockSyncMonitor monitor;
        try
        {
            // 只启动宿主，不创建 HTTP 客户端、不配置采集单元。
            monitor = Assert.Single(factory.Services.GetServices<IHostedService>().OfType<ClockSyncMonitor>());
            Assert.Same(clock, factory.Services.GetRequiredService<IClockSyncStatus>());
            Assert.Equal(1, await clock.Reads.Reader.ReadAsync(token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), token));
            Assert.Equal(2, await clock.Reads.Reader.ReadAsync(token).AsTask().WaitAsync(TimeSpan.FromSeconds(15), token));
        }
        finally
        {
            await factory.DisposeAsync();
        }

        await monitor.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5), token);
        Assert.True(monitor.ExecuteTask.IsCompletedSuccessfully);
        Assert.False(clock.Reads.Reader.TryRead(out _));
    }

    private sealed class ObservedClock : IClockSyncStatus
    {
        private int _reads;
        public Channel<int> Reads { get; } = Channel.CreateUnbounded<int>();
        public ClockSyncState State
        {
            get
            {
                Reads.Writer.TryWrite(Interlocked.Increment(ref _reads));
                return ClockSyncState.Unknown;
            }
        }
    }
}
