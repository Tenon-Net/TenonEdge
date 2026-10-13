using System.Globalization;
using Microsoft.Data.Sqlite;
using TenonEdge.Host.Features.Auth;
using TenonEdge.Host.Features.Deliveries;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Security;
using TenonEdge.Host.Storage;
using TenonEdge.Host.Storage.Ids;

namespace TenonEdge.Host.Bootstrap;

/// <summary>
/// 开始接收请求之前的启动检查与初始化，按顺序执行：
/// <list type="number">
/// <item>校验监听端口与地址（T0-06）、审计保留期（T0-09）、运行时的种子快照与稳定时间（T1-01）等部署参数。</item>
/// <item>取得数据目录与单实例锁；之后的步骤才可以打开数据目录中的运行数据（任务 T0-04）。</item>
/// <item>在容器中运行时检查数据目录是否位于持久卷上，不满足时登记运行时启动条件（T0-06）。</item>
/// <item>初始化配置库：记录 SQLite 版本、启用 WAL、执行版本化迁移（T0-05）。</item>
/// <item>加载或生成网关身份，以库中最大 ID 与下限初始化雪花 ID 生成器（T0-06）。</item>
/// <item>把已登记的运行时启动条件记为运行事件（T0-09）。</item>
/// <item>准备密钥环并自检，加载或生成 HTTPS 证书（T0-06）。</item>
/// <item>开放首次设置窗口；状态不是“已初始化”时输出一行不含秘密的提示并记运行事件（T0-07、T0-09）。</item>
/// <item>登记服务启动与停止的运行事件（T0-09）。</item>
/// <item>按监听设置加入 HTTP 与 HTTPS 端点（T0-06）。</item>
/// </list>
/// 可预期的失败（<see cref="HostStartupException"/>）只记录一条说明原因的错误日志，释放宿主后返回约定的退出码；
/// 访问配置库时的 SQLite 错误（例如配置库被其他程序长时间锁定）按配置库不可用处理，退出码 4。
/// 其他异常按未处理异常结束进程，保留堆栈以便诊断。
/// </summary>
internal static partial class HostStartup
{
    private const string LoggerCategory = "TenonEdge.Host.Startup";

    /// <returns><see cref="HostExitCodes.Success"/> 表示可以开始接收请求；其他值为进程应使用的退出码。</returns>
    public static async Task<int> PrepareAsync(this WebApplication app)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(LoggerCategory);
        try
        {
            _ = app.Services.GetRequiredService<ListenerSettings>();
            _ = app.Services.GetRequiredService<DataRetentionSettings>();
            _ = app.Services.GetRequiredService<RuntimeSettings>();
            var dataDirectory = app.Services.GetRequiredService<DataDirectoryLock>().Directory;
            LogDataDirectoryOpened(logger, dataDirectory.Root);
            var runtimeStartConditions = app.Services.GetRequiredService<RuntimeStartConditions>();
            ContainerDataVolume.Check(dataDirectory, runtimeStartConditions, logger);

            app.Services.GetRequiredService<ConfigStoreInitializer>().Initialize();
            _ = app.Services.GetRequiredService<GatewayIdentity>();
            _ = app.Services.GetRequiredService<SnowflakeIdGenerator>();
            _ = app.Services.GetRequiredService<DeliveryExportManager>(); // 持有数据目录锁后、监听前清除上次导出残留。
            var runtimeEvents = app.Services.GetRequiredService<RuntimeEventLog>();
            RecordRuntimeStartBlockers(runtimeStartConditions, runtimeEvents);
            SecretProtection.EnsureKeyRing(app.Services, logger);
            _ = app.Services.GetRequiredService<HttpsCertificateStore>().Certificate;
            OpenSetupWindow(app.Services, runtimeEvents, logger);
            RecordServiceLifetime(app.Services.GetRequiredService<IHostApplicationLifetime>(), runtimeEvents);
            KestrelListeners.Configure(app.Services);
            return HostExitCodes.Success;
        }
        catch (HostStartupException exception)
        {
            return await RejectAsync(app, logger, exception.ExitCode, exception.Message);
        }
        catch (SqliteException exception)
        {
            return await RejectAsync(app, logger, HostExitCodes.ConfigStoreUnavailable, $"配置库不可用：{exception.Message}");
        }
    }

    private static async Task<int> RejectAsync(WebApplication app, ILogger logger, int exitCode, string reason)
    {
        LogStartupRejected(logger, exitCode, reason);

        // 释放服务容器：已取得的锁随之释放，控制台日志在进程退出前写出。
        await app.DisposeAsync();
        return exitCode;
    }

    private static void RecordRuntimeStartBlockers(RuntimeStartConditions conditions, RuntimeEventLog runtimeEvents)
    {
        foreach (var blocker in conditions.Blockers)
        {
            runtimeEvents.Record(RuntimeEventLevel.Error, RuntimeEventTypes.RuntimeBlocked, blocker.Code, blocker.Reason);
        }
    }

    private static void OpenSetupWindow(IServiceProvider services, RuntimeEventLog runtimeEvents, ILogger logger)
    {
        var window = services.GetRequiredService<SetupWindow>();
        window.Open();
        var status = services.GetRequiredService<SetupService>().GetStatus();
        var closesAt = window.ClosesAt.ToString("O", CultureInfo.InvariantCulture);
        if (status.State == SetupStates.Uninitialized)
        {
            LogSetupWindowOpen(logger, window.ClosesAt);
            runtimeEvents.Record(
                RuntimeEventLevel.Warning,
                RuntimeEventTypes.SetupWindowOpen,
                target: null,
                $"尚未设置管理员，首次设置窗口已开放，将于 {closesAt} 关闭。");
        }
        else if (status.State == SetupStates.ResetPending)
        {
            var accountName = status.ResetAccountName ?? "（未知）";
            LogResetSetupWindowOpen(logger, accountName, window.ClosesAt);
            runtimeEvents.Record(
                RuntimeEventLevel.Warning,
                RuntimeEventTypes.SetupWindowOpen,
                status.ResetAccountName,
                $"管理员账号 {accountName} 已在主机上重置，设置窗口已开放，将于 {closesAt} 关闭。");
        }
    }

    /// <summary>服务启动完成、开始停止时各记一条运行事件（总设计 §3.3“服务启停”）；非正常退出没有停止事件。</summary>
    private static void RecordServiceLifetime(IHostApplicationLifetime lifetime, RuntimeEventLog runtimeEvents)
    {
        lifetime.ApplicationStarted.Register(() => runtimeEvents.Record(
            RuntimeEventLevel.Info,
            RuntimeEventTypes.ServiceStarted,
            target: null,
            $"服务已启动，版本 {ProductVersion.Current}。"));
        lifetime.ApplicationStopping.Register(() => runtimeEvents.Record(
            RuntimeEventLevel.Info,
            RuntimeEventTypes.ServiceStopping,
            target: null,
            "服务正在停止。"));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "尚未设置管理员：首次设置窗口已开放，将于 {ClosesAt:O} 关闭。请在此之前通过网页完成设置；窗口关闭后需重启服务才会重新开放。")]
    private static partial void LogSetupWindowOpen(ILogger logger, DateTimeOffset closesAt);

    [LoggerMessage(Level = LogLevel.Warning, Message = "管理员账号 {AccountName} 已在主机上重置：设置窗口已开放，将于 {ClosesAt:O} 关闭。请在此之前通过网页为该账号设置新密码。")]
    private static partial void LogResetSetupWindowOpen(ILogger logger, string accountName, DateTimeOffset closesAt);

    [LoggerMessage(Level = LogLevel.Information, Message = "已取得数据目录 {DataDirectory} 的单实例锁。")]
    private static partial void LogDataDirectoryOpened(ILogger logger, string dataDirectory);

    [LoggerMessage(Level = LogLevel.Critical, Message = "启动检查未通过，宿主退出（退出码 {ExitCode}）：{Reason}")]
    private static partial void LogStartupRejected(ILogger logger, int exitCode, string reason);
}
