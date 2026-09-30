using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using TenonEdge.Host.Features.Auth;
using TenonEdge.Host.Features.Health;
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
        builder.Services.AddHostedService<DataRetentionService>();

        builder.Services.AddAuthFeature();

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
