namespace TenonEdge.Host.Bootstrap;

/// <summary>
/// 宿主进程的退出码。启动检查中可预期的失败使用固定的非零值，便于服务管理器、容器编排和运维脚本区分原因；
/// 1 以及未处理异常导致的退出码留给 .NET 运行时本身。
/// </summary>
internal static class HostExitCodes
{
    public const int Success = 0;

    /// <summary>部署参数错误：未配置数据目录、路径不是绝对路径或不可用、文件锁被关闭等。</summary>
    public const int ConfigurationError = 2;

    /// <summary>数据目录的单实例锁被另一个进程持有：服务已在运行，或另一个实例、维护命令正在使用同一目录。</summary>
    public const int DataDirectoryInUse = 3;

    /// <summary>配置库无法打开、迁移失败，或由更新版本的程序迁移过（不支持降级）。</summary>
    public const int ConfigStoreUnavailable = 4;

    /// <summary>主机侧维护命令因业务条件未能执行，例如要重置的账号不存在；原因写在标准错误中。</summary>
    public const int MaintenanceCommandFailed = 5;

    /// <summary>
    /// 后台服务因未处理的异常结束，宿主随之停止（.NET 默认的 StopHost 行为）。等待停机的调用此时正常返回，
    /// 不改退出码时进程会以 0 结束，服务管理器按“失败时重启”配置时不会重启（docs/implementation-status.md §6 决定 45）。
    /// </summary>
    public const int BackgroundServiceFailed = 6;

    /// <summary>宿主停止之后进程应使用的退出码：任一后台服务的执行任务以故障结束时为 <see cref="BackgroundServiceFailed"/>，否则为 0。</summary>
    public static int AfterShutdown(IEnumerable<IHostedService> hostedServices) =>
        hostedServices.OfType<BackgroundService>().Any(service => service.ExecuteTask is { IsFaulted: true })
            ? BackgroundServiceFailed
            : Success;
}
