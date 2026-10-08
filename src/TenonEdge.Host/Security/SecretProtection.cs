using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Logging.Debug;
using Microsoft.Extensions.Logging.EventLog;
using Microsoft.Extensions.Logging.EventSource;
using TenonEdge.Host.Storage;

namespace TenonEdge.Host.Security;

/// <summary>
/// 秘密保护的基础（总设计 §10.3，T0-06）：ASP.NET Core Data Protection。
/// <list type="bullet">
/// <item>固定应用名 TenonEdge：安装路径或容器变化后，仍能解密此前保护的数据。</item>
/// <item>密钥环显式持久化在数据目录的 keys 子目录（权限 700），不回退到临时密钥；框架默认不删除旧密钥，本实现也不清理。</item>
/// <item>Linux 上密钥文件不加密落盘：这套机制防的是只拿到数据库文件的泄露，不防整个数据目录被取走（总设计 §10.3）。
/// 因此框架在创建密钥时记录的“未配置密钥加密器”警告是预期的，处理方式见 docs/implementation-status.md §6。</item>
/// <item>框架的 XmlKeyManager 在某个密钥元素解析失败时，会在 Trace 级别把完整的 &lt;key&gt; 元素写入日志，其中含未加密的主密钥
/// （aspnetcore v10.0.0 XmlKeyManager.WriteKeyDeserializationErrorToLog）。因此该类别的最低级别固定为 Debug（见 <see cref="KeyManagerLogRule"/>）：
/// 对 <see cref="KeyManagerLogProviders"/> 中的日志提供程序，部署参数中的日志规则（包括带通配符的规则）都不能把它调回 Trace；
/// 宿主登记新的日志提供程序时须加入该清单，测试会核对。Error 级别的那一条只含去掉子元素的密钥元素。</item>
/// </list>
/// 同一个密钥环也用于认证 Cookie（T0-07 起）。连接凭证等业务秘密随 T1 落地，使用 <see cref="Purposes"/> 中各自的用途字符串相互隔离。
/// </summary>
internal static partial class SecretProtection
{
    public const string ApplicationName = "TenonEdge";

    public const string KeyRingDirectoryName = "keys";

    /// <summary>会在 Trace 级别输出完整密钥元素的框架日志类别。</summary>
    public const string KeyManagerLogCategory = "Microsoft.AspNetCore.DataProtection.KeyManagement.XmlKeyManager";

    /// <summary>
    /// 限制 <see cref="KeyManagerLogCategory"/> 的规则所用的类别模式“类别*类别”。Microsoft.Extensions.Logging 按以下顺序选规则：
    /// 指定了提供程序的规则优先；其次模式越长越优先（只比较模式字符串的长度，前缀与后缀可以重叠）；长度相同时后加入者优先。
    /// 只含一个通配符、又能匹配该类别的模式中，“类别*类别”最长。因此在全部选项配置之后（PostConfigure）为不指定提供程序的情况
    /// 与 <see cref="KeyManagerLogProviders"/> 中的各日志提供程序分别加入这条规则，部署参数中的任何规则都不会胜过它。
    /// </summary>
    public const string KeyManagerLogRule = KeyManagerLogCategory + "*" + KeyManagerLogCategory;

    /// <summary>
    /// 加入上述规则的日志提供程序：ASP.NET Core 默认登记的控制台、调试器输出、EventSource，以及 Windows 上的 EventLog。
    /// 指定了提供程序的规则优先于不指定的，不在清单中的提供程序仍可被部署参数单独调到 Trace。
    /// </summary>
    public static IReadOnlyList<Type> KeyManagerLogProviders { get; } =
        [typeof(ConsoleLoggerProvider), typeof(DebugLoggerProvider), typeof(EventSourceLoggerProvider), typeof(EventLogLoggerProvider)];

    public static IServiceCollection AddSecretProtection(this IServiceCollection services)
    {
        services.PostConfigure<LoggerFilterOptions>(options =>
        {
            options.Rules.Add(new LoggerFilterRule(providerName: null, KeyManagerLogRule, LogLevel.Debug, filter: null));
            foreach (var provider in KeyManagerLogProviders)
            {
                options.Rules.Add(new LoggerFilterRule(provider.FullName, KeyManagerLogRule, LogLevel.Debug, filter: null));
            }
        });
        services.AddDataProtection().SetApplicationName(ApplicationName);
        services.AddOptions<KeyManagementOptions>()
            .Configure<DataDirectoryLock, ILoggerFactory>((options, dataDirectory, loggerFactory) =>
                options.XmlRepository = new FileSystemXmlRepository(
                    new DirectoryInfo(KeyRingDirectory(dataDirectory.Directory)),
                    loggerFactory));
        return services;
    }

    public static string KeyRingDirectory(DataDirectory dataDirectory) => Path.Combine(dataDirectory.Root, KeyRingDirectoryName);

    /// <summary>
    /// 启动检查：以只有属主可访问的权限准备密钥环目录，确认可以取得密钥并完成一次保护与解除保护。
    /// 首次启动时由此生成第一个密钥。
    /// </summary>
    public static void EnsureKeyRing(IServiceProvider services, ILogger logger)
    {
        var dataDirectory = services.GetRequiredService<DataDirectoryLock>().Directory;
        var keyRingDirectory = KeyRingDirectory(dataDirectory);
        PrivateFileSystem.CreateDirectory(keyRingDirectory);

        var protector = services.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purposes.StartupCheck);
        var probe = Guid.NewGuid().ToString("N");
        if (protector.Unprotect(protector.Protect(probe)) != probe)
        {
            throw new InvalidOperationException("密钥环自检失败：解除保护的结果与原文不一致。");
        }

        var keyCount = services.GetRequiredService<IKeyManager>().GetAllKeys().Count;
        LogKeyRingReady(logger, keyRingDirectory, keyCount);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "密钥环位于 {KeyRingDirectory}，共 {KeyCount} 个密钥。")]
    private static partial void LogKeyRingReady(ILogger logger, string keyRingDirectory, int keyCount);

    /// <summary>各类秘密的用途字符串；不同用途的密文不能互相解开。发布后不得修改已有的值。</summary>
    public static class Purposes
    {
        public const string StartupCheck = "TenonEdge.KeyRing.StartupCheck.v1";

        public const string MqttCredential = "TenonEdge.MqttCredential.v1";

        public const string HttpsCertificate = "TenonEdge.HttpsCertificate.v1";
    }
}
