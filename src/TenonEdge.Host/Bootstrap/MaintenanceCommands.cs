using Microsoft.Data.Sqlite;
using TenonEdge.Host.Features.Auth;
using TenonEdge.Host.Security;
using TenonEdge.Host.Storage;

namespace TenonEdge.Host.Bootstrap;

/// <summary>
/// 主机侧维护命令（总设计 §3.1、§10.3）：与服务共用数据目录锁，服务运行时拒绝执行（退出码 3）。
/// <list type="bullet">
/// <item><c>reset-admin [--account &lt;账号&gt;]</c>：为管理员（默认 admin）清除密码并重新开放设置窗口，见 <see cref="AdministratorReset"/>。</item>
/// </list>
/// 其余参数与服务相同（例如 <c>--TenonEdge:DataDirectory</c>），也可以用同样的环境变量提供。
/// 命令只输出结果与原因，不输出任何密码；配置文件从程序所在目录读取，与当前工作目录无关。
/// </summary>
internal static class MaintenanceCommands
{
    public const string ResetAdministrator = "reset-admin";

    private const string AccountOption = "--account";

    public static bool IsMaintenanceCommand(string[] args) => args.Length > 0 && args[0] == ResetAdministrator;

    public static async Task<int> RunAsync(string[] args)
    {
        var accountName = AccountNames.DefaultAdministrator;
        var hostArguments = new List<string>();
        for (var index = 1; index < args.Length; index++)
        {
            if (args[index] == AccountOption)
            {
                if (index + 1 >= args.Length)
                {
                    await Console.Error.WriteLineAsync($"用法：{ResetAdministrator} [{AccountOption} <账号>] [--TenonEdge:DataDirectory <数据目录>]");
                    return HostExitCodes.ConfigurationError;
                }

                accountName = args[++index];
            }
            else
            {
                hostArguments.Add(args[index]);
            }
        }

        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = [.. hostArguments],
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddDataServices();
        builder.Services.AddSingleton<AdministratorReset>();

        using var host = builder.Build();
        try
        {
            _ = host.Services.GetRequiredService<DataDirectoryLock>();
            host.Services.GetRequiredService<ConfigStoreInitializer>().Initialize();
            var user = host.Services.GetRequiredService<AdministratorReset>().Reset(accountName);
            await Console.Out.WriteLineAsync($"已清除管理员账号 {user.AccountName} 的密码，并记入审计。");
            await Console.Out.WriteLineAsync("启动服务后 30 分钟内，在网页的设置页为该账号设置新密码；其他管理员不受影响。");
            return HostExitCodes.Success;
        }
        catch (HostStartupException exception)
        {
            await Console.Error.WriteLineAsync($"{ResetAdministrator} 未执行：{exception.Message}");
            return exception.ExitCode;
        }
        catch (SqliteException exception)
        {
            await Console.Error.WriteLineAsync($"{ResetAdministrator} 未执行：配置库不可用：{exception.Message}");
            return HostExitCodes.ConfigStoreUnavailable;
        }
    }
}
