using TenonEdge.Abstractions.Contracts;
using TenonEdge.Adapters.Http;
using TenonEdge.Adapters.Modbus;
using TenonEdge.Adapters.Mqtt;
using TenonEdge.Adapters.Simulator;
using TenonEdge.Host.Features.Audit;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Credentials;
using TenonEdge.Host.Features.Deliveries;
using TenonEdge.Host.Features.Routes;
using TenonEdge.Host.Features.SystemInfo;
using TenonEdge.Host.Storage;
using TenonEdge.Runtime.Connections;
using TenonEdge.Runtime.Deliveries;
using TenonEdge.Runtime.Diagnostics;
using TenonEdge.Runtime.Inputs;
using TenonEdge.Runtime.Lifecycle;
using TenonEdge.Runtime.Mapping;
using TenonEdge.Runtime.Outputs;
using TenonEdge.Runtime.Transforms;

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

        services.AddSingleton<IClockSyncStatus>(provider => new ClockSyncStatus(
            provider.GetRequiredService<TimeProvider>(), provider.GetRequiredService<IRuntimeEventSink>(), ClockSyncStatus.ReadKernel));
        services.AddHostedService<ClockSyncMonitor>();
        services.AddSingleton<RuntimeTrendHistory>();
        services.AddSingleton<RuntimeMessageCache>();
        services.AddSingleton(provider => new InputChannelLimit(provider.GetRequiredService<RuntimeOptions>().InputChannelCapacity));
        services.AddSingleton(provider =>
        {
            var options = provider.GetRequiredService<RuntimeOptions>();
            return new InFlightBudget(options.InFlightBytes, options.InputBufferBytes);
        });
        services.AddHostedService<RuntimeTrendMonitor>();
        services.AddSingleton<IInputUnitFactory, SimulatorInputFactory>();
        services.AddSingleton<IJsonInputDecoderFactory, JsonInputDecoderFactory>();
        services.AddSingleton<IInputUnitFactory, HttpInputFactory>();
        services.AddSingleton(_ => new RuntimeOptions { InputBufferBytes = 8L * 1024 * 1024 }.Validate());
        services.AddSingleton(provider => new DeliveryCache(
            new DeliveryCacheOptions
            {
                DatabasePath = Path.Combine(provider.GetRequiredService<DataDirectoryLock>().Directory.Root, DeliveryCacheOptions.FileName),
                ConfigStoreId = provider.GetRequiredService<GatewayIdentity>().ConfigStoreId,
            },
            provider.GetRequiredService<TimeProvider>(), events: provider.GetRequiredService<IRuntimeEventSink>(),
            policy: provider.GetRequiredService<IDeliveryPolicySource>()));
        services.AddSingleton<OutputControlStore>();
        services.AddSingleton<DeliveryMigrationStore>();
        services.AddSingleton<DeliveryMigrationPreparation>();
        services.AddSingleton<TenonEdge.Host.Features.Configuration.ConfigurationMigrationPreparation>();
        services.AddSingleton<TenonEdge.Host.Features.Configuration.ConfigurationMigrationIntentService>();
        services.AddSingleton<AuditExportFiles>();
        services.AddSingleton<AuditExportManager>();
        services.AddSingleton<DeliveryExportFiles>();
        services.AddSingleton<DeliveryExportManager>();
        services.AddSingleton<IOutputControl>(provider => provider.GetRequiredService<OutputControlStore>());
        services.AddSingleton<MqttCredentialStore>();
        services.AddSingleton<IMqttSubscriptionStore, MqttSubscriptionStore>();
        services.AddSingleton<HttpCredentialStore>();
        services.AddSingleton<ConnectionTrustStore>();
        services.AddSingleton<ITlsTrustResolver>(provider => provider.GetRequiredService<ConnectionTrustStore>());
        services.AddSingleton<IHttpCredentialResolver>(provider => provider.GetRequiredService<HttpCredentialStore>());
        services.AddSingleton<OutboundPolicyStore>();
        services.AddSingleton(provider => new HostOutboundAddressPolicy(
            provider.GetRequiredService<IConfiguration>(), provider.GetRequiredService<TenonEdge.Host.Bootstrap.ListenerSettings>(),
            provider.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>(), provider.GetRequiredService<OutboundPolicyStore>().Read));
        services.AddSingleton<IOutboundAddressPolicy>(provider => provider.GetRequiredService<HostOutboundAddressPolicy>());
        services.AddScoped<OutboundPolicyInspector>();
        services.AddSingleton<IOutboundConnector, OutboundConnector>();
        services.AddSingleton<ISharedConnections, SharedConnections>();
        services.AddSingleton(provider => new PreviewOperationBudget(readLimits: () => provider.GetRequiredService<RuntimeParameterStore>().Read().Values.Preview));
        services.AddSingleton<MqttConnectionTest>();
        services.AddSingleton<HttpConnectionTest>();
        services.AddSingleton<HttpTestMessageSender>();
        services.AddSingleton<MqttTestMessageSender>();
        services.AddSingleton<RouteTestSendArtifacts>();
        services.AddSingleton<RouteTestSendService>();
        services.AddSingleton<ModbusConnectionTest>();
        services.AddSingleton<ModbusReadPreview>();
        services.AddSingleton<HttpReadPreview>();
        services.AddSingleton(provider => new MqttReadPreview(
            provider.GetRequiredService<ISharedConnections>(), provider.GetRequiredService<IOutboundConnector>(),
            provider.GetRequiredService<ICredentialResolver>(), provider.GetRequiredService<IJsonInputDecoderFactory>(),
            provider.GetRequiredService<RuntimeHost>().InFlight, provider.GetRequiredService<TimeProvider>(), provider.GetRequiredService<ITlsTrustResolver>()));
        services.AddSingleton<IRuntimePlanAdapterProvider, ModbusPlanAdapterProvider>();
        services.AddSingleton<ICredentialResolver>(provider => provider.GetRequiredService<MqttCredentialStore>());
        services.AddSingleton<IRuntimePlanAdapterProvider>(provider =>
        {
            var deliveries = provider.GetRequiredService<DeliveryCache>();
            var events = provider.GetRequiredService<IRuntimeEventSink>();
            return new MqttPlanAdapterProvider(
                provider.GetRequiredService<ICredentialResolver>(), provider.GetRequiredService<IJsonInputDecoderFactory>(),
                provider.GetRequiredService<ISharedConnections>(), provider.GetRequiredService<IOutboundConnector>(),
                provider.GetRequiredService<TimeProvider>(), provider.GetRequiredService<GatewayIdentity>().NodeId,
                (connectionId, reason) =>
                {
                    deliveries.RecordMqttInputDiagnostic(connectionId, reason);
                    if (reason == "sessionLost")
                    {
                        events.Record(RuntimeEventSeverity.Warning, "mqtt.sessionLost",
                            connectionId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            "MQTT 会话丢失，断线期间的消息可能已丢失；需要重新订阅。");
                    }
                }, provider.GetRequiredService<IMqttSubscriptionStore>(), provider.GetRequiredService<ITlsTrustResolver>());
        });
        services.AddSingleton<IOutputAdapterFactory>(provider => new HttpOutputFactory(
            provider.GetRequiredService<IHttpCredentialResolver>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<IOutboundConnector>(),
            provider.GetRequiredService<ISharedConnections>(), provider.GetRequiredService<ITlsTrustResolver>()));
        services.AddSingleton(provider => new TransformCatalog(provider.GetServices<IMessageTransformFactory>()));
        services.AddSingleton<ConnectionModuleCatalog>();
        services.AddSingleton(provider => new InputModuleCatalog(provider.GetServices<IInputModuleFactory>(), provider.GetRequiredService<ConnectionModuleCatalog>()));
        services.AddSingleton(provider => new OutputModuleCatalog(provider.GetServices<IOutputModuleFactory>(), provider.GetRequiredService<ConnectionModuleCatalog>()));
        services.AddSingleton<RuntimeHost>();
        services.AddSingleton<RuntimeVersionStore>();
        services.AddSingleton<RuntimeCoordinator>();
        services.AddHostedService(provider => provider.GetRequiredService<RuntimeCoordinator>());
        return services;
    }
}
