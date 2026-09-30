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
}
