using TenonEdge.Host.Security;

namespace TenonEdge.Host.Features.Auth;

/// <summary>首次设置、登录与账号相关服务的注册（T0-07），均为宿主级单例，自身不保存请求状态。</summary>
internal static class AuthRegistration
{
    public static IServiceCollection AddAuthFeature(this IServiceCollection services)
    {
        services.AddSessionAuthentication();
        services.AddSingleton<SessionSecurityState>();
        services.AddAuthorizationPolicies();
        services.AddLoginRateLimiting();
        services.AddRequestSecurity();
        services.AddSingleton<PasswordHashing>();
        services.AddSingleton<LoginLockout>();
        services.AddSingleton<SetupWindow>();
        services.AddSingleton<SetupService>();
        services.AddSingleton<LoginService>();
        services.AddSingleton<AccountService>();
        return services;
    }
}
