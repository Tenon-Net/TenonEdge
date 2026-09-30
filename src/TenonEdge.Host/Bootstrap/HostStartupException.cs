namespace TenonEdge.Host.Bootstrap;

/// <summary>
/// 启动检查中可预期的失败，例如部署参数错误或数据目录已被占用。
/// 由 <see cref="HostStartup"/> 统一记录一条说明原因的错误日志，并以 <see cref="ExitCode"/> 结束进程，不输出堆栈。
/// </summary>
public sealed class HostStartupException : Exception
{
    public HostStartupException()
        : this(HostExitCodes.ConfigurationError, "宿主启动检查失败。")
    {
    }

    public HostStartupException(string message)
        : this(HostExitCodes.ConfigurationError, message)
    {
    }

    public HostStartupException(string message, Exception innerException)
        : this(HostExitCodes.ConfigurationError, message, innerException)
    {
    }

    public HostStartupException(int exitCode, string message, Exception? innerException = null)
        : base(message, innerException) => ExitCode = exitCode;

    public int ExitCode { get; }
}
