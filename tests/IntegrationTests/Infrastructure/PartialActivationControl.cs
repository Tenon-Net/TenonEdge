using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Plans;
using TenonEdge.Adapters.Mqtt;
using TenonEdge.Adapters.Simulator;
using TenonEdge.Host.Features.Runtime;

namespace TenonEdge.IntegrationTests.Infrastructure;

/// <summary>仅测试进程：全部单元已受监督、激活屏障已释放，另一路尚未调用原循环。
/// 模拟输入可独立采样；共享 MQTT 则须等全部原输入循环启动后才连接，保持原持久接管/恢复分组。
/// 可逐实例显式令慢 MQTT 包装实例在原循环调用前失败，并延后原监督者重建实例的调用；不替换原释放/退避。
/// 原激活事件先真实保存，再固定其返回窗口；不谎称第二路未注册或处于 stopped。</summary>
internal static class PartialActivationControl
{
    internal const string ArmedFile = "partial-activation.armed";
    internal const string InputEnteredFile = "partial-activation.input-entered";
    internal const string InputReleasedFile = "partial-activation.input-released";
    internal const string LifecycleEnteredFile = "partial-activation.lifecycle-entered";
    internal const string LifecycleReleasedFile = "partial-activation.lifecycle-released";
    internal const string FaultRequestedFile = "mqtt-startup-fault.requested";
    internal const string FaultDisposedFile = "mqtt-startup-fault.original-disposed";
    internal const string RestartEnteredFile = "mqtt-startup-fault.restart-entered";
    internal const string RestartReleasedFile = "mqtt-startup-fault.restart-released";
    internal const string RestartOriginalCalledFile = "mqtt-startup-fault.restart-original-called";
    internal const string EventType = "runtime.versionActivated";

    // 故障实例从 1 编号；重试 gate 从 1 编号，保留首轮既有文件名供单次故障及无故障对照使用。
    internal static string AttemptFile(string file, int attempt) => attempt == 1 ? file : $"{file}.{attempt}";

    public static void Register(IServiceCollection services, string directory, bool mqtt = false)
    {
        if (mqtt)
        {
            var registration = services.Single(service => service.ServiceType == typeof(IRuntimePlanAdapterProvider) &&
                service.ImplementationFactory is not null);
            if (registration.Lifetime != ServiceLifetime.Singleton)
            { throw new InvalidOperationException("原 MQTT 计划提供者登记已变化，须重核共享激活探针。"); }
            var build = registration.ImplementationFactory!;
            services.Remove(registration);
            services.AddSingleton<IRuntimePlanAdapterProvider>(provider =>
                new Provider(build(provider) as MqttPlanAdapterProvider ?? throw new InvalidOperationException("须包装原 MQTT 计划提供者。"), directory));
        }
        else
        {
            var simulator = services.Single(service => service.ServiceType == typeof(IInputUnitFactory) &&
                service.ImplementationType == typeof(SimulatorInputFactory));
            services.Remove(simulator);
            services.AddSingleton<IInputUnitFactory>(provider =>
                new Factory(ActivatorUtilities.CreateInstance<SimulatorInputFactory>(provider), directory));
        }
        var events = services.Single(service => service.ServiceType == typeof(IRuntimeEventSink));
        if (events.ImplementationType != typeof(RuntimeEventSink) || events.Lifetime != ServiceLifetime.Singleton)
        { throw new InvalidOperationException("原运行事件入口登记已变化，须重核部分激活探针。"); }
        services.Remove(events);
        services.AddSingleton<IRuntimeEventSink>(provider =>
            new EventSink(ActivatorUtilities.CreateInstance<RuntimeEventSink>(provider), directory));
    }

    private sealed class Provider(IRuntimePlanAdapterProvider inner, string directory) : IRuntimePlanAdapterProvider
    {
        public RuntimePlanAdapters Create()
        {
            var plan = inner.Create();
            if (plan.Inputs.Count != 1 || plan.Inputs[0].Kind != "input.mqtt")
            { throw new InvalidOperationException("原 MQTT 计划输入工厂集合已变化，须重核共享激活探针。"); }
            return new([new Factory(plan.Inputs[0], directory)], plan.Outputs);
        }
    }

    private sealed class Factory(IInputUnitFactory inner, string directory) : IInputUnitFactory
    {
        private int _attempt;
        public string Kind => inner.Kind;
        public bool UsesDurableHandoff => inner.UsesDurableHandoff;
        public RuntimeUnitId RecoveryGroup(RuntimeUnitPlan plan) => inner.RecoveryGroup(plan);
        public IRuntimeUnit Build(RuntimeUnitPlan plan, InputUnitContext context)
        {
            var unit = inner.Build(plan, context);
            if (plan.Id.Key != ConfigurationInterruptionProcessHost.SlowUnitKey || !File.Exists(Path.Combine(directory, ArmedFile)))
            { return unit; }
            var attempt = Interlocked.Increment(ref _attempt);
            Console.WriteLine($"TEST PARTIAL ACTIVATION INPUT BUILT: {unit.Id}; attempt={attempt}");
            return new DelayedInput(unit, context, directory, attempt);
        }
    }

    private sealed class DelayedInput(IRuntimeUnit inner, InputUnitContext context, string directory, int attempt) : IRuntimeUnit
    {
        private bool _faulted;
        public RuntimeUnitId Id => inner.Id;
        public async Task RunAsync(CancellationToken stoppingToken)
        {
            await context.WaitForActivationAsync(stoppingToken); // 不提前采样，也不绕过产品的激活屏障。
            var restart = attempt > 1;
            File.WriteAllText(Path.Combine(directory, restart ? AttemptFile(RestartEnteredFile, attempt - 1) : InputEnteredFile), Id.ToString());
            Console.WriteLine($"TEST {(restart ? "MQTT STARTUP RETRY" : "PARTIAL ACTIVATION INPUT")} ENTERED: {Id}; attempt={attempt}");
            var elapsed = Stopwatch.StartNew();
            while (!File.Exists(Path.Combine(directory, restart ? AttemptFile(RestartReleasedFile, attempt - 1) : InputReleasedFile)))
            {
                if (elapsed.Elapsed >= TimeSpan.FromSeconds(60))
                { throw new TimeoutException("测试部分输入激活同步点未在期限内释放。"); }
                await Task.Delay(10, stoppingToken);
            }
            if (File.Exists(Path.Combine(directory, AttemptFile(FaultRequestedFile, attempt))))
            {
                _faulted = true;
                Console.WriteLine($"TEST MQTT STARTUP FAULT INJECTED: {Id}; attempt={attempt}; original loop not called");
                throw new InvalidOperationException("仅测试：原 MQTT 输入循环调用前的启动异常。");
            }
            Console.WriteLine($"TEST {(restart ? "MQTT STARTUP RETRY" : "PARTIAL ACTIVATION INPUT")} RELEASED: {Id}; attempt={attempt}");
            var loop = inner.RunAsync(stoppingToken);
            if (restart)
            {
                // 原 MQTT RunAsync 的同步前段先标记接收/Started 再等待 I/O；只证明实际调用，不冒充采样成功。
                File.WriteAllText(Path.Combine(directory, AttemptFile(RestartOriginalCalledFile, attempt - 1)), Id.ToString());
                Console.WriteLine($"TEST MQTT STARTUP RETRY ORIGINAL CALLED: {Id}; attempt={attempt}");
            }
            await loop;
        }
        public async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            if (_faulted)
            {
                File.WriteAllText(Path.Combine(directory, AttemptFile(FaultDisposedFile, attempt)), Id.ToString());
                Console.WriteLine($"TEST MQTT STARTUP FAULT ORIGINAL DISPOSED: {Id}; attempt={attempt}");
            }
        }
    }

    private sealed class EventSink(IRuntimeEventSink inner, string directory) : IRuntimeEventSink
    {
        public void Record(RuntimeEventSeverity severity, string type, string? target, string description)
        {
            inner.Record(severity, type, target, description); // 原事件已保存、事务已结束后才固定返回窗口。
            if (type != EventType || !File.Exists(Path.Combine(directory, ArmedFile)))
            { return; }
            File.WriteAllText(Path.Combine(directory, LifecycleEnteredFile), type);
            Console.WriteLine($"TEST PARTIAL ACTIVATION LIFECYCLE ENTERED: {description}");
            var elapsed = Stopwatch.StartNew();
            while (!File.Exists(Path.Combine(directory, LifecycleReleasedFile)))
            {
                if (elapsed.Elapsed >= TimeSpan.FromSeconds(60))
                {
                    // 事件入口不注入异常；父测试的 30 秒等待/标记检查会失败，不能把超时当作正常释放。
                    Console.WriteLine("TEST PARTIAL ACTIVATION LIFECYCLE DEADLINE EXPIRED");
                    return;
                }
                Thread.Sleep(10);
            }
            Console.WriteLine("TEST PARTIAL ACTIVATION LIFECYCLE RELEASED");
        }
    }
}
