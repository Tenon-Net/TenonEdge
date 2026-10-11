using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Abstractions.Plans;
using TenonEdge.Adapters.Simulator;
using TenonEdge.Host.Bootstrap;
using TenonEdge.Host.Storage;

namespace TenonEdge.IntegrationTests.Infrastructure;

/// <summary>仅测试程序集的入口：普通参数完全交给原 xUnit runner；明确的子进程命令才启动受控适配器工厂。
/// 不复制产品装配，不增加测试专用产品 API、配置开关或运行单元种类。重启验证仍运行原产品 Host。</summary>
internal static class ConfigurationInterruptionProcessHost
{
    internal const string Command = "--tenonedge-test-preparation-host";
    internal const string PartialStopCommand = "--tenonedge-test-partial-stop-host";
    internal const string PreInputActivationCommand = "--tenonedge-test-pre-input-activation-host";
    internal const string GateDirectoryVariable = "TENONEDGE_TEST_PREPARATION_DIRECTORY";
    internal const string ArmedFile = "preparation.armed";
    internal const string EnteredFile = "preparation.entered";
    internal const string ReleasedFile = "preparation.released";
    internal const string StopArmedFile = "partial-stop.armed";
    internal const string StopEnteredFile = "partial-stop.entered";
    internal const string StopReleasedFile = "partial-stop.released";
    internal const string StopDisposedFile = "partial-stop.disposed";
    internal const string ActivationArmedFile = "pre-input-activation.armed";
    internal const string ActivationEnteredFile = "pre-input-activation.entered";
    internal const string ActivationReleasedFile = "pre-input-activation.released";
    internal const string SlowDeviceKey = "meter-slow";
    internal const string SlowInputKey = "slow-stop";
    internal const string SlowUnitKey = SlowDeviceKey + "/" + SlowInputKey;
    internal const string SlowIdentity = "input.simulator/" + SlowUnitKey;

    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is not (Command or PartialStopCommand or PreInputActivationCommand))
        { return await Xunit.Runner.InProc.SystemConsole.ConsoleRunner.Run(args); }
        if (!OperatingSystem.IsLinux())
        { throw new PlatformNotSupportedException("受控配置中断进程只在 Linux 验证。"); }
        var partialStop = args[0] == PartialStopCommand;
        var preInputActivation = args[0] == PreInputActivationCommand;
        var gate = Environment.GetEnvironmentVariable(GateDirectoryVariable);
        if (gate is null || !Path.IsPathFullyQualified(gate) || !Directory.Exists(gate))
        { throw new ArgumentException("测试同步目录必须是已存在的绝对路径。"); }
        Console.WriteLine($"TEST {(preInputActivation ? "PRE-INPUT ACTIVATION" : partialStop ? "PARTIAL STOP" : "PREPARATION")} DIRECTORY: {gate}");
        var configuration = new ConfigurationBuilder().AddCommandLine(args[1..]).Build();
        var data = configuration[DataDirectory.ConfigurationKey] ?? throw new ArgumentException("须提供独立测试数据目录。");
        var settings = configuration.AsEnumerable().Where(pair => pair.Value is not null)
            .ToDictionary(pair => pair.Key, pair => pair.Value!, StringComparer.OrdinalIgnoreCase);
        await using var factory = new FixedDataDirectoryApplicationFactory(data, settings, services =>
        {
            if (preInputActivation)
            {
                // 当前唯一的进程级输出工厂是 HTTP；复用原登记的构造与依赖，不创建第二个发送者或连接管理器。
                var output = services.Single(service => service.ServiceType == typeof(IOutputAdapterFactory));
                var build = output.ImplementationFactory ?? throw new InvalidOperationException("HTTP 输出工厂登记已变化，须重核探针。");
                services.Remove(output);
                services.AddSingleton<IOutputAdapterFactory>(provider =>
                    new ControlledHttpOutputFactory((IOutputAdapterFactory)build(provider), gate));
                return;
            }
            var simulator = services.Single(service => service.ServiceType == typeof(IInputUnitFactory) &&
                service.ImplementationType == typeof(SimulatorInputFactory));
            services.Remove(simulator);
            services.AddSingleton<IInputUnitFactory>(new ControlledSimulatorFactory(gate, partialStop));
        });
        factory.UseKestrel();
        factory.StartServer();
        var services = factory.Services;
        var lifetime = services.GetRequiredService<IHostApplicationLifetime>();
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exitCode = HostExitCodes.Success;
        using var stoppedRegistration = lifetime.ApplicationStopped.Register(() =>
        {
            exitCode = HostExitCodes.AfterShutdown(services.GetServices<IHostedService>());
            stopped.TrySetResult();
        });
        using var signal = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
        {
            context.Cancel = true;
            lifetime.StopApplication();
        });
        // 即使调用它的测试进程意外消失，探针也不会永久留在后台；生产 Host 不使用本期限。
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        using var timeoutRegistration = deadline.Token.Register(lifetime.StopApplication);
        await stopped.Task;
        return exitCode;
    }

    private sealed class ControlledHttpOutputFactory(IOutputAdapterFactory inner, string directory) : IOutputAdapterFactory
    {
        public string Kind => inner.Kind;
        public RuntimeUnitId? RecoveryGroup(RuntimeUnitPlan plan) => inner.RecoveryGroup(plan);
        public IOutputTarget Build(RuntimeUnitPlan plan)
        {
            if (Kind != "output.http")
            { throw new InvalidOperationException("提交后输入启动探针只包装真实 HTTP 输出。"); }
            return new ControlledHttpOutputTarget(inner.Build(plan), directory);
        }
    }

    private sealed class ControlledHttpOutputTarget(IOutputTarget inner, string directory) : IOutputTarget
    {
        private IOutputTarget Inner => inner;
        public RuntimeUnitId Id => inner.Id;
        public long ConnectionId => inner.ConnectionId;
        public IReadOnlyList<string> Inputs => inner.Inputs;
        public string? ClientId => inner.ClientId;
        public string Recipient => inner.Recipient;
        public string CredentialVersion => inner.CredentialVersion;
        public string CurrentFingerprint => inner.CurrentFingerprint;
        public long FormattingReservationBytes => inner.FormattingReservationBytes;
        public long MinimumSerializationReservationBytes => inner.MinimumSerializationReservationBytes;
        public bool QuiesceWithInputs => inner.QuiesceWithInputs;
        public OutputMessage CreateMessage(EdgeMessage message, Guid deliveryId) => inner.CreateMessage(message, deliveryId);
        public IOutputAdapter CreateAdapter() => inner.CreateAdapter();

        public bool CanShareSenderWith(IOutputTarget other)
        {
            // 先按真实类型与参数判断，解包仅为维持 HTTP 工厂原有的类型检查；不改变是否沿用发送者的结果。
            var share = inner.CanShareSenderWith(other is ControlledHttpOutputTarget wrapped ? wrapped.Inner : other);
            if (share && File.Exists(Path.Combine(directory, ActivationArmedFile)))
            {
                // 在线激活协调发送者时配置提交已经返回，所有旧输入/分发已经停止，新输入尚未交给监督者。
                File.WriteAllText(Path.Combine(directory, ActivationEnteredFile), Id.ToString());
                Console.WriteLine($"TEST PRE-INPUT ACTIVATION GATE ENTERED: {Id}");
                var elapsed = Stopwatch.StartNew();
                while (!File.Exists(Path.Combine(directory, ActivationReleasedFile)))
                {
                    if (elapsed.Elapsed >= TimeSpan.FromSeconds(60))
                    { throw new TimeoutException("测试提交后输入启动同步点未在期限内释放。"); }
                    Thread.Sleep(10);
                }
                Console.WriteLine($"TEST PRE-INPUT ACTIVATION GATE RELEASED: {Id}");
            }
            return share;
        }
    }

    private sealed class ControlledSimulatorFactory(string directory, bool partialStop) : IInputUnitFactory
    {
        private readonly SimulatorInputFactory _inner = new();
        public string Kind => _inner.Kind;

        public IRuntimeUnit Build(RuntimeUnitPlan unit, InputUnitContext context)
        {
            if (!partialStop && File.Exists(Path.Combine(directory, ArmedFile)))
            {
                // 正式模拟工厂尚未构造新实例；只延长这次本地 Build 调用，旧输入和真实发送链路不替换。
                File.WriteAllText(Path.Combine(directory, EnteredFile), unit.Id.ToString());
                Console.WriteLine($"TEST PREPARATION GATE ENTERED: {unit.Id}");
                var elapsed = Stopwatch.StartNew();
                while (!File.Exists(Path.Combine(directory, ReleasedFile)))
                {
                    if (elapsed.Elapsed >= TimeSpan.FromSeconds(60))
                    { throw new TimeoutException("测试准备同步点未在期限内释放。"); }
                    Thread.Sleep(10);
                }
                Console.WriteLine($"TEST PREPARATION GATE RELEASED: {unit.Id}");
            }
            var instance = _inner.Build(unit, context);
            return partialStop && unit.Id.Key == SlowUnitKey
                ? new PartialStopUnit(instance, directory) : instance;
        }
    }

    private sealed class PartialStopUnit(IRuntimeUnit inner, string directory) : IRuntimeUnit
    {
        public RuntimeUnitId Id => inner.Id;

        public async Task RunAsync(CancellationToken stoppingToken)
        {
            await inner.RunAsync(stoppingToken);
            if (!stoppingToken.IsCancellationRequested || !File.Exists(Path.Combine(directory, StopArmedFile)))
            { return; }
            // 正式模拟循环已响应取消，但这个包装实例尚未退出/释放。另一输入由原工厂完整停止。
            // 产品仍按原 5 秒期限监督；测试必须先核对 stopped/stopping，而不能把超时当作部分停止。
            File.WriteAllText(Path.Combine(directory, StopEnteredFile), Id.ToString());
            Console.WriteLine($"TEST PARTIAL STOP GATE ENTERED: {Id}");
            var elapsed = Stopwatch.StartNew();
            while (!File.Exists(Path.Combine(directory, StopReleasedFile)))
            {
                if (elapsed.Elapsed >= TimeSpan.FromSeconds(60))
                { throw new TimeoutException("测试部分停止同步点未在期限内释放。"); }
                await Task.Delay(10, CancellationToken.None);
            }
            Console.WriteLine($"TEST PARTIAL STOP GATE RELEASED: {Id}");
        }

        public async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            File.WriteAllText(Path.Combine(directory, StopDisposedFile), Id.ToString());
            Console.WriteLine($"TEST PARTIAL STOP UNIT DISPOSED: {Id}");
        }
    }
}
