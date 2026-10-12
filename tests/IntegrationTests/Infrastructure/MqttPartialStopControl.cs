using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Plans;
using TenonEdge.Adapters.Mqtt;

namespace TenonEdge.IntegrationTests.Infrastructure;

/// <summary>仅测试进程：延后一路原 MQTT 循环收到取消，而非在它已经退出后假装还在停止。
/// 另一路照常停止并静默/排空/断开真实共享接收器；原计划工厂的持久接管、恢复分组与输出集合不变。</summary>
internal static class MqttPartialStopControl
{
    internal const string ArmedFile = "mqtt-partial-stop.armed";
    internal const string EnteredFile = "mqtt-partial-stop.entered";
    internal const string ReleasedFile = "mqtt-partial-stop.released";

    public static void Register(IServiceCollection services, string directory)
    {
        var registration = services.Single(service => service.ServiceType == typeof(IRuntimePlanAdapterProvider) &&
            service.ImplementationFactory is not null);
        if (registration.Lifetime != ServiceLifetime.Singleton)
        { throw new InvalidOperationException("原 MQTT 计划提供者登记已变化，须重核共享停止探针。"); }
        var build = registration.ImplementationFactory!;
        services.Remove(registration);
        services.AddSingleton<IRuntimePlanAdapterProvider>(provider =>
            new Provider(build(provider) as MqttPlanAdapterProvider ?? throw new InvalidOperationException("须包装原 MQTT 计划提供者。"), directory));
    }

    private sealed class Provider(IRuntimePlanAdapterProvider inner, string directory) : IRuntimePlanAdapterProvider
    {
        public RuntimePlanAdapters Create()
        {
            var plan = inner.Create();
            if (plan.Inputs.Count != 1 || plan.Inputs[0].Kind != "input.mqtt")
            { throw new InvalidOperationException("原 MQTT 计划输入工厂集合已变化，须重核探针。"); }
            return new([new Factory(plan.Inputs[0], directory)], plan.Outputs);
        }
    }

    private sealed class Factory(IInputUnitFactory inner, string directory) : IInputUnitFactory
    {
        public string Kind => inner.Kind;
        public bool UsesDurableHandoff => inner.UsesDurableHandoff;
        public RuntimeUnitId RecoveryGroup(RuntimeUnitPlan plan) => inner.RecoveryGroup(plan);
        public IRuntimeUnit Build(RuntimeUnitPlan plan, InputUnitContext context)
        {
            var unit = inner.Build(plan, context);
            return plan.Id.Key == ConfigurationInterruptionProcessHost.SlowUnitKey ? new DelayedStop(unit, directory) : unit;
        }
    }

    private sealed class DelayedStop(IRuntimeUnit inner, string directory) : IRuntimeUnit
    {
        private bool _wasStarted;
        public RuntimeUnitId Id => inner.Id;
        public async Task RunAsync(CancellationToken stoppingToken)
        {
            stoppingToken.ThrowIfCancellationRequested();
            _wasStarted = true;
            using var originalStop = new CancellationTokenSource();
            var stopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = stoppingToken.Register(() => stopping.TrySetResult());
            var running = inner.RunAsync(originalStop.Token);
            try
            {
                await Task.WhenAny(running, stopping.Task);
                if (!running.IsCompleted && File.Exists(Path.Combine(directory, ArmedFile)))
                {
                    File.WriteAllText(Path.Combine(directory, EnteredFile), Id.ToString());
                    Console.WriteLine($"TEST MQTT PARTIAL STOP GATE ENTERED: {Id}; original cancellation not forwarded");
                    var elapsed = Stopwatch.StartNew();
                    while (!File.Exists(Path.Combine(directory, ReleasedFile)))
                    {
                        if (running.IsCompleted)
                        { throw new InvalidOperationException("原 MQTT 循环在未转发取消前已经退出，部分停止窗口失效。"); }
                        if (elapsed.Elapsed >= TimeSpan.FromSeconds(60))
                        { throw new TimeoutException("测试共享 MQTT 部分停止同步点未在期限内释放。"); }
                        await Task.Delay(10, CancellationToken.None);
                    }
                    Console.WriteLine($"TEST MQTT PARTIAL STOP GATE RELEASED: {Id}");
                }
            }
            finally
            {
                await originalStop.CancelAsync();
                await running; // 原停止/排空/共享租约释放仍由原单元完成；故障不被包装吞掉。
                Console.WriteLine($"TEST MQTT PARTIAL STOP ORIGINAL STOPPED: {Id}");
            }
        }
        public async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            // 放弃的候选计划也会释放从未启动的包装单元，不能误把它标成原运行实例已释放。
            Console.WriteLine(_wasStarted ? $"TEST MQTT PARTIAL STOP UNIT DISPOSED: {Id}"
                : $"TEST MQTT PARTIAL STOP UNSTARTED UNIT DISPOSED: {Id}");
        }
    }
}
