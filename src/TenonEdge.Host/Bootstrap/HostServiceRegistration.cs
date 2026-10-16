using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using TenonEdge.Host.Features.Auth;
using TenonEdge.Host.Features.Configuration.Transfer;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Devices;
using TenonEdge.Host.Features.Health;
using TenonEdge.Host.Features.Inputs;
using TenonEdge.Host.Features.Routes;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Features.SystemInfo;
using TenonEdge.Host.Security;
using TenonEdge.Host.Storage;

namespace TenonEdge.Host.Bootstrap;

/// <summary>
/// 宿主服务注册入口。只使用宿主统一的依赖注入容器，按模块显式装配。
/// </summary>
internal static class HostServiceRegistration
{
    public static WebApplicationBuilder AddTenonEdgeHostServices(this WebApplicationBuilder builder)
    {
        // 所有环境都开启作用域与构建时校验：单例不得持有请求作用域服务，注册错误在启动时暴露（后端实施规范 §4）。
        builder.Host.UseDefaultServiceProvider(options =>
        {
            options.ValidateScopes = true;
            options.ValidateOnBuild = true;
        });

        builder.Services.AddDataServices();
        builder.Services.AddSecretProtection();
        builder.Services.AddSingleton(provider => ListenerSettings.Resolve(provider.GetRequiredService<IConfiguration>()));
        builder.Services.AddSingleton<HttpsCertificateStore>();

        // 审计与运行事件按保留期清理（T0-09）；保留期参数在启动检查中校验。
        builder.Services.AddSingleton(provider => DataRetentionSettings.Resolve(provider.GetRequiredService<IConfiguration>()));
        builder.Services.AddSingleton<RuntimeParameterStore>();
        builder.Services.AddSingleton<QueueParameterStore>();
        builder.Services.AddSingleton<TrustedProxyStore>();
        builder.Services.AddSingleton<TenonEdge.Abstractions.Contracts.IDeliveryPolicySource>(provider => provider.GetRequiredService<QueueParameterStore>());
        builder.Services.AddSingleton<Func<TimeSpan>>(provider => () => TimeSpan.FromDays(provider.GetRequiredService<RuntimeParameterStore>().Read().Values.AuditRetentionDays));
        builder.Services.AddHostedService<DataRetentionService>();

        builder.Services.AddAuthFeature();
        builder.Services.AddScoped<GatewayNameStore>();
        builder.Services.AddSingleton(TenonEdge.Adapters.Modbus.ModbusConnectionParameters.Schema);
        builder.Services.AddSingleton(TenonEdge.Adapters.Mqtt.MqttConnectionParameters.Schema);
        builder.Services.AddSingleton<TenonEdge.Host.Features.Parameters.ParameterSchemaCatalog>();
        builder.Services.AddSingleton<ConnectionDraftStore>();
        builder.Services.AddSingleton<DeviceDraftStore>();
        builder.Services.AddSingleton<InputDraftStore>();
        builder.Services.AddSingleton<RouteDraftStore>();
        builder.Services.AddSingleton<ConfigurationExportStore>();
        builder.Services.AddSingleton<ConfigurationImportStore>();
        builder.Services.AddSingleton<ConfigurationImportPreparation>();

        // 运行时（T1-01—T1-03）：协调者、运行时宿主、发送缓存与输入适配器。
        builder.Services.AddRuntimeFeature();

        // 枚举按字符串序列化（总设计 §10.1），OpenAPI 文档同样按字符串描述。
        builder.Services.ConfigureHttpJsonOptions(options =>
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));

        builder.Services.AddProblemDetails();
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<IAuthorizationMiddlewareResultHandler, ApiAuthorizationResultHandler>();
        builder.Services.AddHealthEndpointChecks();
        builder.Services.AddManagementApiDocument();

        return builder;
    }
}
