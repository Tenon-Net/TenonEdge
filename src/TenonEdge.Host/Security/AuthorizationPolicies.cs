namespace TenonEdge.Host.Security;

/// <summary>
/// 按角色集中定义的授权策略（总设计 §10.3，后端实施规范 §3.2）。除只读状态查询外，接口都声明所需角色：
/// <list type="bullet">
/// <item><see cref="Administrator"/>：仅管理员，例如审计查询、用户与系统设置。</item>
/// <item><see cref="Configurator"/>：管理员与配置人员，例如修改配置（T1 起）。</item>
/// <item>默认策略：任何已登录的角色，例如查看会话、修改自己的密码、系统信息。</item>
/// </list>
/// 未登录返回 401，已登录但角色不符返回 403（<see cref="ApiAuthorizationResultHandler"/>）。
/// </summary>
internal static class AuthorizationPolicies
{
    public const string Administrator = "role:administrator";

    public const string Configurator = "role:configurator";

    public static IServiceCollection AddAuthorizationPolicies(this IServiceCollection services)
    {
        services.AddAuthorizationBuilder()
            .AddPolicy(Administrator, policy => policy.RequireAuthenticatedUser().RequireRole(nameof(UserRole.Administrator)))
            .AddPolicy(Configurator, policy => policy.RequireAuthenticatedUser()
                .RequireRole(nameof(UserRole.Administrator), nameof(UserRole.Configurator)));
        return services;
    }
}
