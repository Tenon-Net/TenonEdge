using System.Runtime.InteropServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Parameters;
using TenonEdge.Adapters.Http;
using TenonEdge.Host.Bootstrap;
using TenonEdge.Host.Storage;
using TenonEdge.IntegrationTests.Runtime;

namespace TenonEdge.IntegrationTests.Infrastructure;

/// <summary>仅测试侧编译期登记转换、输入与输出模块，原 Program、管理 API、工厂与运行时全部保留。
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
                .AddSingleton(BrowserConnection())
                .AddSingleton<IInputModuleFactory>(provider => new CredentialWireModule(
                    provider.GetRequiredService<IModuleCredentialResolver>(), provider.GetRequiredService<ISharedConnections>(),
                    provider.GetRequiredService<IOutboundConnector>())
                { Automatic = true })
                .AddSingleton<IOutputModuleFactory>(BrowserOutput("http"))
                .AddSingleton<IOutputModuleFactory>(BrowserOutput("mqtt"))
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

    private static ConnectionModuleDefinition BrowserConnection() => new("authwire", 1, "测试凭证连接",
        new("connection.authwire", ParameterScope.Connection,
        [
            new("host", "主机", ParameterType.Text) { Required = true, MaximumLength = 255 },
            new("port", "端口", ParameterType.Number) { Required = true, WholeNumber = true, MinimumJson = "1", MaximumJson = "65535" },
            new("precision", "精确参数", ParameterType.Number) { Required = true, WholeNumber = true, DefaultJson = "9007199254740993", MinimumJson = "9007199254740990", MaximumJson = "9007199254740999" },
            new("flag", "测试开关", ParameterType.Switch) { DefaultJson = "true" },
            new("mode", "测试选项", ParameterType.Choice) { DefaultJson = "\"one\"", Options = [new("one", "选项一"), new("two", "选项二")] },
            new("auth", "认证秘密", ParameterType.Credential) { Required = true },
        ]), ["host", "port"]);

    private static TestProtocolOutputModule BrowserOutput(string protocol) => new(protocol)
    {
        Version = 2,
        DisplayName = "测试 " + protocol.ToUpperInvariant() + " 输出",
        Parameters = new("output.test" + protocol, ParameterScope.Output,
        [
            new("connection", "目标连接引用", ParameterType.ConnectionReference) { Required = true, ConnectionKinds = [protocol] },
            new("address", "路径或主题", ParameterType.Text) { Required = true, MaximumLength = 1024, DefaultJson = "\"events\"" },
            new("precision", "精确参数", ParameterType.Number) { Required = true, WholeNumber = true, DefaultJson = "9007199254740993", MinimumJson = "9007199254740990", MaximumJson = "9007199254740999" },
            new("flag", "测试开关", ParameterType.Switch) { DefaultJson = "true" },
            new("mode", "测试选项", ParameterType.Choice) { DefaultJson = "\"one\"", Options = [new("one", "选项一"), new("two", "选项二")] },
        ]),
    };
}
