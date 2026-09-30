using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace TenonEdge.IntegrationTests.Infrastructure;

/// <summary>
/// 以独立进程运行已构建的宿主（与测试相同的构建配置，程序与 appsettings.json 位于测试输出目录），
/// 检查进程内测试无法证明的真实端口监听、启动与停机行为。
/// 监听端口由操作系统分配（端口 0），实际地址从宿主的启动日志读取，避免预先挑选端口带来的竞争。
/// </summary>
public sealed partial class HostProcess : IAsyncDisposable
{
    private const string HostAssemblyFileName = "TenonEdge.Host.dll";
    private const string ReadinessPath = "/readyz";

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    private readonly Process _process;
    private readonly StringBuilder _output = new();
    private readonly Lock _outputLock = new();
    private readonly TaskCompletionSource<Uri> _listeningAddress = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private HostProcess(Process process) => _process = process;

    /// <summary>目前为止收集到的标准输出与标准错误。</summary>
    public string Output
    {
        get
        {
            lock (_outputLock)
            {
                return _output.ToString();
            }
        }
    }

    /// <summary>
    /// 在指定工作目录启动宿主，只监听回环地址上由系统分配的端口。内容根固定为程序所在目录，与工作目录无关。
    /// </summary>
    public static HostProcess Start(string workingDirectory, string environmentName)
    {
        var programDirectory = AppContext.BaseDirectory;
        var startInfo = new ProcessStartInfo(DotNetExecutable.Path)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add(Path.Combine(programDirectory, HostAssemblyFileName));
        startInfo.ArgumentList.Add("--urls");
        startInfo.ArgumentList.Add("http://127.0.0.1:0");
        startInfo.ArgumentList.Add("--contentRoot");
        startInfo.ArgumentList.Add(programDirectory);
        // 保证启动日志中的实际监听地址一定输出，不受日志配置变化影响。
        startInfo.ArgumentList.Add("--Logging:LogLevel:Microsoft.Hosting.Lifetime=Information");
        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = environmentName;
        startInfo.Environment.Remove("DOTNET_ENVIRONMENT");
        startInfo.Environment.Remove("ASPNETCORE_URLS");

        var process = new Process { StartInfo = startInfo };
        try
        {
            var host = new HostProcess(process);
            process.OutputDataReceived += (_, e) => host.OnOutput(e.Data);
            process.ErrorDataReceived += (_, e) => host.OnOutput(e.Data);
            if (!process.Start())
            {
                throw new InvalidOperationException("宿主进程未能启动。");
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            return host;
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    /// <summary>等待宿主报告实际监听地址；进程提前退出或超时均失败，并附带进程输出。</summary>
    public async Task<Uri> WaitForListeningAddressAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            var listening = _listeningAddress.Task.WaitAsync(timeoutSource.Token);
            var exited = _process.WaitForExitAsync(timeoutSource.Token);
            if (await Task.WhenAny(listening, exited) == listening)
            {
                return await listening;
            }

            // WaitForExitAsync 完成时已读完重定向的输出，错误信息包含完整日志。
            await exited;
            throw new InvalidOperationException(
                $"宿主进程在开始监听前退出，退出码 {_process.ExitCode}。{Environment.NewLine}{Output}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"宿主在 {timeout.TotalSeconds} 秒内没有报告监听地址。{Environment.NewLine}{Output}");
        }
    }

    /// <summary>
    /// 轮询就绪端点直到返回 200。开始监听与完成启动之间短暂返回 503 属于正常情况；
    /// 进程提前退出或超时则失败，并附带进程输出。
    /// </summary>
    public async Task WaitUntilReadyAsync(HttpClient client, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < timeout)
        {
            if (_process.HasExited)
            {
                await _process.WaitForExitAsync(cancellationToken);
                throw new InvalidOperationException(
                    $"宿主进程在就绪前退出，退出码 {_process.ExitCode}。{Environment.NewLine}{Output}");
            }

            using (var response = await client.GetAsync(new Uri(ReadinessPath, UriKind.Relative), cancellationToken))
            {
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    return;
                }
            }

            await Task.Delay(PollInterval, cancellationToken);
        }

        throw new TimeoutException($"宿主在 {timeout.TotalSeconds} 秒内没有就绪。{Environment.NewLine}{Output}");
    }

    /// <summary>发送 SIGTERM，与 systemd 或容器发出的正常停止请求相同。</summary>
    public async Task RequestStopAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("发送 SIGTERM 只在 Linux 上实现。");
        }

        var startInfo = new ProcessStartInfo("kill") { UseShellExecute = false };
        startInfo.ArgumentList.Add("-TERM");
        startInfo.ArgumentList.Add(_process.Id.ToString(CultureInfo.InvariantCulture));

        using var kill = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动 kill 命令。");
        await kill.WaitForExitAsync(cancellationToken);
        if (kill.ExitCode != 0)
        {
            throw new InvalidOperationException($"kill 命令失败，退出码 {kill.ExitCode}。");
        }
    }

    /// <summary>等待进程退出并返回退出码；超时则失败，并附带进程输出。</summary>
    public async Task<int> WaitForExitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await _process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"宿主在 {timeout.TotalSeconds} 秒内没有退出。{Environment.NewLine}{Output}");
        }

        return _process.ExitCode;
    }

    public async ValueTask DisposeAsync()
    {
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync();
        }

        _process.Dispose();
    }

    private void OnOutput(string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (_outputLock)
        {
            _output.AppendLine(line);
        }

        var match = ListeningAddressLine().Match(line);
        if (match.Success)
        {
            _listeningAddress.TrySetResult(new Uri(match.Groups["address"].Value));
        }
    }

    [GeneratedRegex(@"Now listening on: (?<address>http://127\.0\.0\.1:\d+)")]
    private static partial Regex ListeningAddressLine();

    /// <summary>
    /// dotnet 主机程序：优先使用 .NET CLI 设置的 DOTNET_HOST_PATH，否则由当前共享运行时目录推出。
    /// </summary>
    private static class DotNetExecutable
    {
        public static string Path { get; } = Locate();

        private static string Locate()
        {
            var hostPath = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
            if (!string.IsNullOrEmpty(hostPath) && File.Exists(hostPath))
            {
                return hostPath;
            }

            // 共享运行时目录形如 <dotnet 根目录>/shared/Microsoft.NETCore.App/<版本>/。
            var runtimeDirectory = new DirectoryInfo(
                System.IO.Path.TrimEndingDirectorySeparator(RuntimeEnvironment.GetRuntimeDirectory()));
            var root = runtimeDirectory.Parent?.Parent?.Parent
                ?? throw new InvalidOperationException($"无法从 {runtimeDirectory.FullName} 推出 dotnet 根目录。");
            var candidate = System.IO.Path.Combine(root.FullName, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");

            return File.Exists(candidate)
                ? candidate
                : throw new InvalidOperationException($"未找到 dotnet 主机程序：{candidate}");
        }
    }
}
