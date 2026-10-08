using TenonEdge.Abstractions.Contracts;
using TenonEdge.Adapters.Mqtt;
using TenonEdge.Adapters.Simulator;
using TenonEdge.Host.Features.Credentials;
using TenonEdge.Host.Storage;
using TenonEdge.Runtime.Deliveries;
using TenonEdge.Runtime.Diagnostics;
using TenonEdge.Runtime.Lifecycle;

namespace TenonEdge.Host.Features.Runtime;

/// <summary>
/// 运行时的装配（T1-01—T1-03，后端实施规范 §4）：运行时、发送缓存与协调者都是宿主级单例；
/// 协调者是唯一的运行时后台服务，按同一个单例登记为托管服务，不会因重复注册产生第二套运行时。
/// 输入适配器按编译期模块显式注册（总设计 §3.2），不扫描外部程序集。
/// </summary>
internal static class RuntimeRegistration
{
    public static IServiceCollection AddRuntimeFeature(this IServiceCollection services)
    {
        services.AddSingleton(provider => RuntimeSettings.Resolve(provider.GetRequiredService<IConfiguration>()));
        services.AddSingleton<IRuntimeEventSink, RuntimeEventSink>();

        // 校时检测在 T1-07 实现（V-14），此前时钟状态为“未知”。
        services.AddSingleton<IClockSyncStatus, UnknownClockSyncStatus>();
        services.AddSingleton<IInputUnitFactory, SimulatorInputFactory>();
        services.AddSingleton(_ => new RuntimeOptions().Validate());
        services.AddSingleton(provider => new DeliveryCache(
            new DeliveryCacheOptions
            {
                DatabasePath = Path.Combine(provider.GetRequiredService<DataDirectoryLock>().Directory.Root, DeliveryCacheOptions.FileName),
                ConfigStoreId = provider.GetRequiredService<GatewayIdentity>().ConfigStoreId,
            },
            provider.GetRequiredService<TimeProvider>()));
        services.AddSingleton<MqttCredentialStore>();
        services.AddSingleton<ICredentialResolver>(provider => provider.GetRequiredService<MqttCredentialStore>());
        services.AddSingleton<IOutputAdapterFactory>(provider => new MqttOutputFactory(
            provider.GetRequiredService<ICredentialResolver>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<GatewayIdentity>().NodeId));
        services.AddSingleton<RuntimeHost>();
        services.AddSingleton<RuntimeVersionStore>();
        services.AddSingleton<RuntimeCoordinator>();
        services.AddHostedService(provider => provider.GetRequiredService<RuntimeCoordinator>());
        return services;
    }
}
