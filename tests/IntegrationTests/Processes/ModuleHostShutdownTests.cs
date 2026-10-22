using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TenonEdge.Host.Bootstrap;
using TenonEdge.IntegrationTests.Infrastructure;

namespace TenonEdge.IntegrationTests.Processes;

public sealed class ModuleHostShutdownTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShutdownResultSurvivesProviderDisposalBeforeWaitingContinuation(bool faulted)
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "模块测试宿主的信号登记要求 Linux。");
        using var data = TemporaryDirectory.Create();
        var background = new ControlledBackgroundService();
        await using var factory = new FixedDataDirectoryApplicationFactory(data.Path, new Dictionary<string, string>
        {
            ["TenonEdge:Http:Address"] = "127.0.0.1",
            ["TenonEdge:Http:Port"] = "0",
            ["TenonEdge:Https:Address"] = "127.0.0.1",
            ["TenonEdge:Https:Port"] = "0",
        }, configureServices:
            services => services.AddSingleton<IHostedService>(background));
        factory.UseKestrel();
        factory.StartServer();
        var services = factory.Services;
        var lifetime = services.GetRequiredService<IHostApplicationLifetime>();
        var continuation = new HeldContinuationContext();
        var original = SynchronizationContext.Current;
        Task<int> shutdown;
        try
        {
            SynchronizationContext.SetSynchronizationContext(continuation);
            shutdown = TransformModuleProcessHost.WaitForShutdownAsync(services);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(original);
        }

        if (faulted)
        {
            background.Fail();
            await Assert.ThrowsAsync<InvalidOperationException>(() => background.ExecuteTask!);
        }
        else
        {
            lifetime.StopApplication();
        }

        // Force the ordering seen in the E2E crash: Program.RunAsync disposes its
        // provider before the module host's asynchronously scheduled waiter resumes.
        // No sleeps, timing retries, swallowed errors, or product fault switches.
        await factory.DisposeAsync();
        Assert.Throws<ObjectDisposedException>(() => services.GetService<IHostApplicationLifetime>());
        await continuation.Posted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.False(shutdown.IsCompleted);
        continuation.Resume();
        Assert.Equal(faulted ? HostExitCodes.BackgroundServiceFailed : HostExitCodes.Success, await shutdown);
    }

    private sealed class HeldContinuationContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _callbacks = new();
        public TaskCompletionSource Posted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override void Post(SendOrPostCallback callback, object? state)
        {
            _callbacks.Enqueue((callback, state));
            Posted.TrySetResult();
        }

        public void Resume()
        {
            Assert.True(_callbacks.TryDequeue(out var continuation));
            continuation.Callback(continuation.State);
            Assert.Empty(_callbacks);
        }
    }

    private sealed class ControlledBackgroundService : BackgroundService
    {
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Fail() => _completion.SetException(new InvalidOperationException("test-module-background-failure"));

        protected override Task ExecuteAsync(CancellationToken stoppingToken) => _completion.Task.WaitAsync(stoppingToken);
    }
}
