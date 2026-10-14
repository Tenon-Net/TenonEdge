using Microsoft.Extensions.DependencyInjection.Extensions;
using TenonEdge.Host.Bootstrap;
using TenonEdge.Host.Features.Audit;
using TenonEdge.Host.Features.Runtime;

namespace TenonEdge.Host.Storage;

/// <summary>
/// 数据相关服务的注册，Web 宿主与主机侧维护命令共用：数据目录锁、时间源、运行时启动条件、配置库、网关身份、雪花 ID，
/// 以及审计与运行事件的写入（写入失败时改写到日志，每类每分钟限额）。
/// </summary>
internal static class DataServices
{
    public static IServiceCollection AddDataServices(this IServiceCollection services)
    {
        // 数据目录只能通过单实例锁取得：依赖它的服务在锁到手之前无法构造（总设计 §3.1）。
        services.AddSingleton(provider => DataDirectoryLock.Acquire(DataDirectory.Resolve(
            provider.GetRequiredService<IConfiguration>(),
            provider.GetRequiredService<IHostEnvironment>())));
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<RuntimeStartConditions>();
        services.AddConfigStore();
        services.AddGatewayIdentity();
        services.AddSingleton<StorageWriteFallback>();
        services.AddSingleton<AuditTrail>();
        services.AddSingleton<AuditRetentionGate>();
        services.AddSingleton<RuntimeEventLog>();
        return services;
    }
}
