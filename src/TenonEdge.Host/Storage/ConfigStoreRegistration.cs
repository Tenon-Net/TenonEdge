using TenonEdge.Host.Storage.Migrations;

namespace TenonEdge.Host.Storage;

/// <summary>配置库相关服务的注册：数据库入口、迁移器与启动初始化，均为宿主级单例。</summary>
internal static class ConfigStoreRegistration
{
    public static IServiceCollection AddConfigStore(this IServiceCollection services)
    {
        services.AddSingleton<ConfigDatabase>();
        services.AddSingleton(provider => new ConfigMigrator(
            provider.GetRequiredService<ConfigDatabase>(),
            ConfigMigrations.All,
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<ILogger<ConfigMigrator>>()));
        services.AddSingleton<ConfigStoreInitializer>();
        return services;
    }
}
