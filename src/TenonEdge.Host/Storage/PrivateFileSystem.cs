namespace TenonEdge.Host.Storage;

/// <summary>
/// 在数据目录中创建只有服务账号可以访问的目录和文件（总设计 §10.3：数据库、密钥和备份放在受限目录）。
/// Windows 只用于开发调试，沿用目录继承的访问控制。
/// </summary>
internal static class PrivateFileSystem
{
    public const UnixFileMode DirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    public const UnixFileMode FileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public static void CreateDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
        }
        else
        {
            Directory.CreateDirectory(path, DirectoryMode);
        }
    }

    /// <summary>文件打开选项；由此新建的文件只有属主可以读写。</summary>
    public static FileStreamOptions Options(System.IO.FileMode mode, FileAccess access, FileShare share)
    {
        var options = new FileStreamOptions { Mode = mode, Access = access, Share = share };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = FileMode;
        }

        return options;
    }
}
