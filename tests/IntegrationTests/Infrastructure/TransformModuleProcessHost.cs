using System.Runtime.InteropServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Adapters.Http;
using TenonEdge.Host.Bootstrap;
using TenonEdge.Host.Storage;
using TenonEdge.IntegrationTests.Runtime;

namespace TenonEdge.IntegrationTests.Infrastructure;

/// <summary>仅测试侧编译期登记转换与输入模块，原 Program、管理 API、工厂与运行时全部保留。
/// 无产品测试开关，不自动创建配置；用于验证无需改前端即可发现/编辑/运行新的纯转换。</summary>
internal static class TransformModuleProcessHost
{
    internal const string Command = "--tenonedge-test-transform-host";
    public static async Task<int> RunAsync(string[] args)
    {
        if (!OperatingSystem.IsLinux())
        { throw new PlatformNotSupportedException("测试子进程要求 Linux。"); }
        var configuration = new ConfigurationBuilder().AddCommandLine(args).Build();
        var data = configuration[DataDirectory.ConfigurationKey] ?? throw new ArgumentException("须提供独立测试目录。");
        var settings = configuration.AsEnumerable().Where(pair => pair.Value is not null).ToDictionary(pair => pair.Key, pair => pair.Value!);
        await using var factory = new FixedDataDirectoryApplicationFactory(data, settings,
            services => services.AddSingleton<IMessageTransformFactory>(new TestOffsetTransformFactory())
                .AddSingleton<IInputModuleFactory>(new TestScalarInputModule())
                .AddSingleton<IInputModuleFactory>(new TestProtocolInputModule("mqtt"))
                .AddSingleton<IInputModuleFactory>(new TestProtocolInputModule("modbus"))
                .AddSingleton<IInputModuleFactory>(provider => new InputModuleConnectionTests.HttpReferenceModule(
                    provider.GetServices<IInputUnitFactory>().OfType<HttpInputFactory>().Single())
                { PeriodMs = 1000 }));
        factory.UseKestrel();
        factory.StartServer();
        var lifetime = factory.Services.GetRequiredService<IHostApplicationLifetime>();
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = lifetime.ApplicationStopped.Register(() => stopped.TrySetResult());
        using var signal = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => { context.Cancel = true; lifetime.StopApplication(); });
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        using var timeout = deadline.Token.Register(lifetime.StopApplication);
        await stopped.Task;
        return HostExitCodes.AfterShutdown(factory.Services.GetServices<IHostedService>());
    }
}
