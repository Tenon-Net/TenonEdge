using TenonEdge.Host.Bootstrap;

namespace TenonEdge.Host.Storage;

/// <summary>
/// 数据目录（总设计 §3.1）：由部署参数显式配置的绝对路径，不依赖进程工作目录。
/// 配置库、密钥环和本机身份文件都放在这里；只有取得 <see cref="DataDirectoryLock"/> 之后才能打开其中的数据。
/// </summary>
internal sealed class DataDirectory
{
    /// <summary>部署参数名；环境变量写作 <c>TenonEdge__DataDirectory</c>。</summary>
    public const string ConfigurationKey = "TenonEdge:DataDirectory";

    public const string LockFileName = "tenonedge.lock";

    private DataDirectory(string root) => Root = root;

    /// <summary>规范化后的绝对路径，不带末尾分隔符。</summary>
    public string Root { get; }

    public string LockFilePath => Path.Combine(Root, LockFileName);

    /// <summary>
    /// 读取并检查部署参数。只接受绝对路径：相对路径会随工作目录变化，指向另一套数据。
    /// 目录须已存在，由安装脚本、服务管理器或容器数据卷提供，这样路径写错时会报错，而不是悄悄新建一套空数据；
    /// 只有 Development 环境在目录缺少时自动创建，便于本机调试。
    /// </summary>
    public static DataDirectory Resolve(IConfiguration configuration, IHostEnvironment environment)
    {
        var configured = configuration[ConfigurationKey];
        if (string.IsNullOrWhiteSpace(configured))
        {
            throw new HostStartupException(
                HostExitCodes.ConfigurationError,
                $"未配置数据目录。请用部署参数 {ConfigurationKey}（环境变量 TenonEdge__DataDirectory）指定绝对路径。");
        }

        if (!Path.IsPathFullyQualified(configured))
        {
            throw new HostStartupException(
                HostExitCodes.ConfigurationError,
                $"数据目录必须是绝对路径，当前配置为“{configured}”。相对路径会随进程工作目录变化而指向另一套数据。");
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(configured));
        if (File.Exists(root))
        {
            throw new HostStartupException(HostExitCodes.ConfigurationError, $"数据目录“{root}”是一个文件，不是目录。");
        }

        if (!Directory.Exists(root))
        {
            if (!environment.IsDevelopment())
            {
                throw new HostStartupException(
                    HostExitCodes.ConfigurationError,
                    $"数据目录“{root}”不存在。请先创建该目录并授予服务账号读写权限；在容器中运行时应挂载数据卷。");
            }

            PrivateFileSystem.CreateDirectory(root);
        }

        return new DataDirectory(root);
    }
}
