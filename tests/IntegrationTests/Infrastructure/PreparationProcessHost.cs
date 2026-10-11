using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Plans;
using TenonEdge.Adapters.Simulator;
using TenonEdge.Host.Bootstrap;
using TenonEdge.Host.Storage;

namespace TenonEdge.IntegrationTests.Infrastructure;

/// <summary>仅测试程序集的入口：普通参数完全交给原 xUnit runner；明确的子进程命令才启动受控准备工厂。
/// 不复制产品装配，不增加测试专用产品 API、配置开关或运行单元种类。重启验证仍运行原产品 Host。</summary>
internal static class PreparationProcessHost
{
    internal const string Command = "--tenonedge-test-preparation-host";
    internal const string GateDirectoryVariable = "TENONEDGE_TEST_PREPARATION_DIRECTORY";
    internal const string ArmedFile = "preparation.armed";
    internal const string EnteredFile = "preparation.entered";
    internal const string ReleasedFile = "preparation.released";

    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] != Command)
        { return await Xunit.Runner.InProc.SystemConsole.ConsoleRunner.Run(args); }
        if (!OperatingSystem.IsLinux())
        { throw new PlatformNotSupportedException("受控准备阶段进程只在 Linux 验证。"); }
        var gate = Environment.GetEnvironmentVariable(GateDirectoryVariable);
        if (gate is null || !Path.IsPathFullyQualified(gate) || !Directory.Exists(gate))
        { throw new ArgumentException("测试同步目录必须是已存在的绝对路径。"); }
        Console.WriteLine($"TEST PREPARATION DIRECTORY: {gate}");
        var configuration = new ConfigurationBuilder().AddCommandLine(args[1..]).Build();
        var data = configuration[DataDirectory.ConfigurationKey] ?? throw new ArgumentException("须提供独立测试数据目录。");
        var settings = configuration.AsEnumerable().Where(pair => pair.Value is not null)
            .ToDictionary(pair => pair.Key, pair => pair.Value!, StringComparer.OrdinalIgnoreCase);
        await using var factory = new FixedDataDirectoryApplicationFactory(data, settings, services =>
        {
            var simulator = services.Single(service => service.ServiceType == typeof(IInputUnitFactory) &&
                service.ImplementationType == typeof(SimulatorInputFactory));
            services.Remove(simulator);
            services.AddSingleton<IInputUnitFactory>(new PreparationGateFactory(gate));
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

    private sealed class PreparationGateFactory(string directory) : IInputUnitFactory
    {
        private readonly SimulatorInputFactory _inner = new();
        public string Kind => _inner.Kind;

        public IRuntimeUnit Build(RuntimeUnitPlan unit, InputUnitContext context)
        {
            if (File.Exists(Path.Combine(directory, ArmedFile)))
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
            return _inner.Build(unit, context);
        }
    }
}
