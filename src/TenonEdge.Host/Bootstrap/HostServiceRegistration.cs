using Microsoft.AspNetCore.Authorization;
using TenonEdge.Host.Features.Health;
using TenonEdge.Host.Security;

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

        builder.Services.AddProblemDetails();
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<IAuthorizationMiddlewareResultHandler, ApiAuthorizationResultHandler>();
        builder.Services.AddHealthEndpointChecks();
        builder.Services.AddManagementApiDocument();

        return builder;
    }
}
