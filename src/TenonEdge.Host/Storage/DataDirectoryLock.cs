using TenonEdge.Host.Bootstrap;

namespace TenonEdge.Host.Storage;

/// <summary>
/// 数据目录的单实例锁（总设计 §3.1、AT-10）：进程存续期间独占持有数据目录中的 <c>tenonedge.lock</c>。
/// <list type="bullet">
/// <item>Linux 上以 FileShare.None 打开文件时，.NET 使用 flock 加独占锁。进程无论以何种方式结束（包括被强制终止），
/// 锁都由内核释放，不依赖可能残留的 PID 文件；同一进程内再次获取同样被拒绝。</item>
/// <item>主机侧维护命令使用同一把锁，所以服务运行时无法执行；雪花 ID 的唯一性也依赖这把锁（总设计 §10.1）。</item>
/// </list>
/// 需要读写数据目录的服务都依赖本类型，以保证先取得锁、再打开数据。
/// </summary>
internal sealed class DataDirectoryLock : IDisposable
{
    private const string DisableFileLockingSwitch = "System.IO.DisableFileLocking";
    private const string DisableFileLockingVariable = "DOTNET_SYSTEM_IO_DISABLEFILELOCKING";

    private readonly FileStream _lockFile;

    private DataDirectoryLock(DataDirectory directory, FileStream lockFile)
    {
        Directory = directory;
        _lockFile = lockFile;
    }

    public DataDirectory Directory { get; }

    public static DataDirectoryLock Acquire(DataDirectory directory)
    {
        EnsureFileLockingEnabled();
        try
        {
            var lockFile = new FileStream(
                directory.LockFilePath,
                PrivateFileSystem.Options(FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
            return new DataDirectoryLock(directory, lockFile);
        }
        catch (IOException exception) when (IsHeldByAnotherHandle(exception))
        {
            throw new HostStartupException(
                HostExitCodes.DataDirectoryInUse,
                $"数据目录“{directory.Root}”正被另一个 TenonEdge 进程使用（单实例锁 {directory.LockFilePath}）。" +
                "同一数据目录只能运行一个实例；执行主机侧维护命令前须先停止服务。",
                exception);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new HostStartupException(
                HostExitCodes.ConfigurationError,
                $"无法在数据目录“{directory.Root}”中创建或打开单实例锁：{exception.Message}",
                exception);
        }
    }

    public void Dispose() => _lockFile.Dispose();

    /// <summary>
    /// 这两项设置会让 FileShare 不再加锁，单实例锁随之失效，因此拒绝启动，而不是在没有保护的情况下继续运行。
    /// </summary>
    private static void EnsureFileLockingEnabled()
    {
        var disabledBySwitch = AppContext.TryGetSwitch(DisableFileLockingSwitch, out var switchValue) && switchValue;
        var variable = Environment.GetEnvironmentVariable(DisableFileLockingVariable);
        var disabledByVariable = variable is not null
            && (variable == "1" || variable.Equals("true", StringComparison.OrdinalIgnoreCase));
        if (disabledBySwitch || disabledByVariable)
        {
            throw new HostStartupException(
                HostExitCodes.ConfigurationError,
                $"文件锁已被关闭（{DisableFileLockingVariable} 或 {DisableFileLockingSwitch}），数据目录的单实例锁无法生效。请移除该设置后再启动。");
        }
    }

    /// <summary>锁被占用时的错误码：Linux 为 EWOULDBLOCK（11），macOS 为 35，Windows 为共享冲突或锁冲突。</summary>
    private static bool IsHeldByAnotherHandle(IOException exception)
    {
        if (OperatingSystem.IsWindows())
        {
            var code = exception.HResult & 0xFFFF;
            return code is 32 or 33;
        }

        return exception.HResult == (OperatingSystem.IsMacOS() ? 35 : 11);
    }
}
