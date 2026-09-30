using System.Buffers;
using System.Text;
using TenonEdge.Host.Storage;

namespace TenonEdge.Host.Bootstrap;

/// <summary>
/// 容器中的数据目录持久化条件（总设计 §10.3，V-08）：在容器中运行时，数据目录须位于已挂载的持久卷上，
/// 否则容器删除或重建后配置与密钥环随之丢失。Data Protection 没有公开的检测接口（S-13），由网关自行检查：
/// <list type="bullet">
/// <item>是否在容器中：Docker 创建 /.dockerenv，Podman 创建 /run/.containerenv，官方 .NET 镜像设置 DOTNET_RUNNING_IN_CONTAINER=true，
/// 遵循 systemd 约定的容器管理器设置 container 环境变量；任一成立即视为在容器中。</item>
/// <item>数据目录所在的挂载点：取 /proc/self/mountinfo 中与数据目录（先解析符号链接）匹配的最长挂载点。
/// 挂载点为容器根目录，或文件系统为 overlay、tmpfs 等不持久的类型时，视为不满足条件。</item>
/// </list>
/// 不满足时登记运行时启动条件（运行时不启动，就绪检查返回 503），并在日志中提示挂载数据卷；管理界面仍可使用。
/// 原生部署不做这项检查。
/// </summary>
internal static partial class ContainerDataVolume
{
    public const string BlockerCode = "data-directory-volume";

    private const string MountInfoPath = "/proc/self/mountinfo";

    private static readonly SearchValues<char> OctalDigits = SearchValues.Create("01234567");

    private static readonly HashSet<string> NonPersistentFileSystems =
        new(["overlay", "overlayfs", "aufs", "tmpfs", "ramfs"], StringComparer.Ordinal);

    public static void Check(DataDirectory dataDirectory, RuntimeStartConditions conditions, ILogger logger)
    {
        if (!OperatingSystem.IsLinux() || !IsRunningInContainer(File.Exists, Environment.GetEnvironmentVariable))
        {
            return;
        }

        var path = ResolveSymbolicLinks(dataDirectory.Root);
        var mount = File.Exists(MountInfoPath) ? FindMount(path, File.ReadAllText(MountInfoPath)) : null;
        if (mount is not null && IsPersistentVolume(mount))
        {
            LogVolumeFound(logger, dataDirectory.Root, mount.MountPoint, mount.FileSystemType);
            return;
        }

        var location = mount is null ? "无法确定所在的挂载点" : $"位于挂载点 {mount.MountPoint}（{mount.FileSystemType}）";
        var reason = $"在容器中运行，但数据目录“{dataDirectory.Root}”不在已挂载的持久卷上（{location}）：" +
            "容器删除或重建后，配置与密钥会一并丢失，运行时不会启动。请为数据目录挂载数据卷后重新创建容器。";
        conditions.Block(BlockerCode, reason);
        LogRuntimeBlocked(logger, reason);
    }

    public static bool IsRunningInContainer(Func<string, bool> fileExists, Func<string, string?> environment) =>
        fileExists("/.dockerenv")
        || fileExists("/run/.containerenv")
        || string.Equals(environment("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase)
        || !string.IsNullOrEmpty(environment("container"));

    public static bool IsPersistentVolume(MountEntry mount) =>
        mount.MountPoint != "/" && !NonPersistentFileSystems.Contains(mount.FileSystemType);

    /// <summary>从 mountinfo 内容中找出包含指定绝对路径的最长挂载点；没有匹配时返回 null。</summary>
    public static MountEntry? FindMount(string path, string mountInfo)
    {
        MountEntry? best = null;
        foreach (var line in mountInfo.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (TryParse(line, out var entry)
                && Contains(entry.MountPoint, path)
                && (best is null || entry.MountPoint.Length >= best.MountPoint.Length))
            {
                best = entry;
            }
        }

        return best;
    }

    /// <summary>
    /// 解析一行 mountinfo（proc(5)）：第 5 个字段为挂载点，可选字段之后以单独的“-”分隔，其后第一个字段为文件系统类型。
    /// 挂载点中的空格、制表符、换行和反斜杠以八进制转义。
    /// </summary>
    public static bool TryParse(string line, out MountEntry entry)
    {
        entry = default!;

        // 至少 10 个字段：6 个固定字段、分隔符、文件系统类型、挂载源和超级块选项。
        var fields = line.Split(' ');
        if (fields.Length < 10)
        {
            return false;
        }

        var separator = Array.IndexOf(fields, "-", 6);
        if (separator < 0 || separator + 1 >= fields.Length)
        {
            return false;
        }

        entry = new MountEntry(Unescape(fields[4]), fields[separator + 1]);
        return true;
    }

    private static bool Contains(string mountPoint, string path) =>
        mountPoint == "/"
        || path == mountPoint
        || path.StartsWith(mountPoint + "/", StringComparison.Ordinal);

    private static string Unescape(string value)
    {
        if (!value.Contains('\\', StringComparison.Ordinal))
        {
            return value;
        }

        var builder = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] == '\\' && IsOctal(value, index + 1))
            {
                builder.Append((char)Convert.ToInt32(value.Substring(index + 1, 3), 8));
                index += 3;
            }
            else
            {
                builder.Append(value[index]);
            }
        }

        return builder.ToString();
    }

    private static bool IsOctal(string value, int start) =>
        start + 3 <= value.Length && value.AsSpan(start, 3).IndexOfAnyExcept(OctalDigits) < 0;

    /// <summary>逐级解析路径中的符号链接，得到挂载表中实际出现的路径。</summary>
    public static string ResolveSymbolicLinks(string path)
    {
        var resolved = "/";
        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(resolved, segment);
            var target = new DirectoryInfo(candidate).ResolveLinkTarget(returnFinalTarget: true);
            resolved = target is null ? candidate : Path.GetFullPath(target.FullName);
        }

        return resolved;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "在容器中运行：数据目录 {DataDirectory} 位于挂载点 {MountPoint}（{FileSystemType}）。")]
    private static partial void LogVolumeFound(ILogger logger, string dataDirectory, string mountPoint, string fileSystemType);

    [LoggerMessage(Level = LogLevel.Error, Message = "运行时启动条件不满足：{Reason}")]
    private static partial void LogRuntimeBlocked(ILogger logger, string reason);
}

/// <summary>mountinfo 中的一个挂载点及其文件系统类型。</summary>
internal sealed record MountEntry(string MountPoint, string FileSystemType);
