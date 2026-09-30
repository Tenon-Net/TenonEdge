using TenonEdge.Host.Storage.Ids;

namespace TenonEdge.Host.Storage;

/// <summary>
/// 网关身份与雪花 ID 生成器的注册（T0-06）。两者都依赖已完成迁移的配置库，由启动检查在配置库初始化之后立即加载。
/// </summary>
internal static partial class GatewayIdentityRegistration
{
    public static IServiceCollection AddGatewayIdentity(this IServiceCollection services)
    {
        services.AddSingleton(provider =>
        {
            var identity = GatewayIdentity.LoadOrCreate(provider.GetRequiredService<ConfigDatabase>(), out var created);
            var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger<GatewayIdentity>();
            if (created)
            {
                LogIdentityCreated(logger, identity.NodeId, identity.ConfigStoreId);
            }
            else
            {
                LogIdentityLoaded(logger, identity.NodeId, identity.ConfigStoreId);
            }

            return identity;
        });

        services.AddSingleton(provider => IdGeneration.CreateGenerator(
            provider.GetRequiredService<ConfigDatabase>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<ILoggerFactory>().CreateLogger<SnowflakeIdGenerator>()));

        return services;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "已生成网关身份：NodeId {NodeId}，configStoreId {ConfigStoreId}。")]
    private static partial void LogIdentityCreated(ILogger logger, string nodeId, string configStoreId);

    [LoggerMessage(Level = LogLevel.Information, Message = "网关身份：NodeId {NodeId}，configStoreId {ConfigStoreId}。")]
    private static partial void LogIdentityLoaded(ILogger logger, string nodeId, string configStoreId);
}
