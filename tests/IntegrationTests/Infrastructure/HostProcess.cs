using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using TenonEdge.Host.Storage;

namespace TenonEdge.IntegrationTests.Infrastructure;

/// <summary>
/// 以独立进程运行已构建的宿主（默认使用测试输出目录中的同一构建，程序与 appsettings.json 都在该目录），
/// 检查进程内测试无法证明的真实端口监听、TLS、启动与停机行为。
/// HTTP 与 HTTPS 都只监听回环地址，端口由操作系统分配（端口 0），实际地址从宿主的启动日志读取，避免预先挑选端口带来的竞争。
/// </summary>
public sealed partial class HostProcess : IAsyncDisposable
{
    private const string HostAssemblyFileName = "TenonEdge.Host.dll";
    private const string ReadinessPath = "/readyz";

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    private readonly Process _process;
    private readonly StringBuilder _output = new();
    private readonly Lock _outputLock = new();
    private readonly TaskCompletionSource<Uri> _httpAddress = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<Uri> _httpsAddress = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

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
    /// 在指定工作目录启动宿主。内容根固定为程序所在目录，与工作目录无关。
    /// </summary>
    /// <param name="dataDirectory">数据目录部署参数的原样取值；传 null 表示不配置。</param>
    /// <param name="additionalArguments">追加在默认参数之后的命令行参数，可覆盖前面的同名参数。</param>
    /// <param name="environmentVariables">额外设置的环境变量。</param>
    /// <param name="programDirectory">宿主程序所在目录；默认为测试输出目录，模拟更换安装路径时传入复制后的目录。</param>
    public static HostProcess Start(
        string workingDirectory,
        string environmentName,
        string? dataDirectory,
        IEnumerable<string>? additionalArguments = null,
        IReadOnlyDictionary<string, string>? environmentVariables = null,
        string? programDirectory = null) => StartCore(workingDirectory, environmentName, dataDirectory, additionalArguments,
            environmentVariables, programDirectory, HostAssemblyFileName, []);

    /// <summary>以测试程序集启动真实 Program/Kestrel，仅替换模拟输入的准备工厂来固定中断窗口。
    /// 默认 Start 仍运行未经注入的产品 Host；此入口、参数和工厂不会进入产品构建。</summary>
    public static HostProcess StartPreparationProbe(string workingDirectory, string dataDirectory, IEnumerable<string> additionalArguments) =>
        StartControlledProbe(workingDirectory, dataDirectory, additionalArguments, ConfigurationInterruptionProcessHost.Command);

    /// <summary>以同一测试入口控制一个真实模拟输入的停止完成；不改变其他输入或产品停止期限。</summary>
    public static HostProcess StartPartialStopProbe(string workingDirectory, string dataDirectory, IEnumerable<string> additionalArguments) =>
        StartControlledProbe(workingDirectory, dataDirectory, additionalArguments, ConfigurationInterruptionProcessHost.PartialStopCommand);

    /// <summary>仅测试侧延长提交后发送者交接判断，固定新输入尚未启动的窗口；其余 HTTP 行为委托原工厂。</summary>
    public static HostProcess StartPreInputActivationProbe(string workingDirectory, string dataDirectory, IEnumerable<string> additionalArguments) =>
        StartControlledProbe(workingDirectory, dataDirectory, additionalArguments, ConfigurationInterruptionProcessHost.PreInputActivationCommand);

    /// <summary>仅测试侧通过原配置库的 SQL 执行后回调固定真实写入、尚未 COMMIT 的事务窗口。</summary>
    public static HostProcess StartTransactionWritingProbe(string workingDirectory, string dataDirectory, IEnumerable<string> additionalArguments) =>
        StartControlledProbe(workingDirectory, dataDirectory, additionalArguments, ConfigurationInterruptionProcessHost.TransactionWritingCommand);

    /// <summary>仅测试侧延后一个正式输入循环，并固定原激活事件的返回；其他输入、输出和事件处理保持原样。</summary>
    public static HostProcess StartPartialActivationProbe(string workingDirectory, string dataDirectory, IEnumerable<string> additionalArguments) =>
        StartControlledProbe(workingDirectory, dataDirectory, additionalArguments, ConfigurationInterruptionProcessHost.PartialActivationCommand);

    /// <summary>仅测试侧延后一路原 MQTT 循环收到取消，另一输入照常静默并断开原共享会话。</summary>
    public static HostProcess StartMqttPartialStopProbe(string workingDirectory, string dataDirectory, IEnumerable<string> additionalArguments) =>
        StartControlledProbe(workingDirectory, dataDirectory, additionalArguments, ConfigurationInterruptionProcessHost.MqttPartialStopCommand);

    private static HostProcess StartControlledProbe(string workingDirectory, string dataDirectory, IEnumerable<string> additionalArguments, string command) =>
        StartCore(workingDirectory, "Production", dataDirectory, additionalArguments,
            new Dictionary<string, string> { [ConfigurationInterruptionProcessHost.GateDirectoryVariable] = workingDirectory }, null,
            "TenonEdge.IntegrationTests.dll", [command]);

    private static HostProcess StartCore(string workingDirectory, string environmentName, string? dataDirectory,
        IEnumerable<string>? additionalArguments, IReadOnlyDictionary<string, string>? environmentVariables,
        string? programDirectory, string assemblyFileName, IEnumerable<string> prefixArguments)
    {
        programDirectory ??= AppContext.BaseDirectory;
        var startInfo = new ProcessStartInfo(DotNetExecutable.Path)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add(Path.Combine(programDirectory, assemblyFileName));
        foreach (var argument in prefixArguments)
        { startInfo.ArgumentList.Add(argument); }
        string[] listeners =
        [
            "--TenonEdge:Http:Address", "127.0.0.1", "--TenonEdge:Http:Port", "0",
            "--TenonEdge:Https:Address", "127.0.0.1", "--TenonEdge:Https:Port", "0",
        ];
        foreach (var argument in listeners)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.ArgumentList.Add("--contentRoot");
        startInfo.ArgumentList.Add(programDirectory);
        // 保证启动日志中的实际监听地址一定输出，不受日志配置变化影响。
        startInfo.ArgumentList.Add("--Logging:LogLevel:Microsoft.Hosting.Lifetime=Information");
        if (dataDirectory is not null)
        {
            startInfo.ArgumentList.Add($"--{DataDirectory.ConfigurationKey}");
            startInfo.ArgumentList.Add(dataDirectory);
        }

        foreach (var argument in additionalArguments ?? [])
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = environmentName;
        foreach (var inherited in new[] { "DOTNET_ENVIRONMENT", "ASPNETCORE_URLS", "DOTNET_URLS", "TenonEdge__DataDirectory" })
        {
            startInfo.Environment.Remove(inherited);
        }

        foreach (var (name, value) in environmentVariables ?? new Dictionary<string, string>())
        {
            startInfo.Environment[name] = value;
        }

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

    /// <summary>
    /// 运行主机侧维护命令（例如 reset-admin）并等待结束，返回退出码与全部输出。命令使用与服务相同的构建。
    /// </summary>
    public static async Task<(int ExitCode, string Output)> RunCommandAsync(
        string workingDirectory,
        IEnumerable<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(DotNetExecutable.Path)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, HostAssemblyFileName));
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment.Remove("TenonEdge__DataDirectory");
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("维护命令未能启动。");
        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"维护命令在 {timeout.TotalSeconds} 秒内没有结束。");
        }

        return (process.ExitCode, await standardOutput + await standardError);
    }

    /// <summary>等待宿主报告 HTTP 监听地址；进程提前退出或超时均失败，并附带进程输出。</summary>
    public Task<Uri> WaitForListeningAddressAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        WaitAsync(_httpAddress.Task, "开始监听 HTTP", timeout, cancellationToken);

    /// <summary>等待宿主报告 HTTPS 监听地址。</summary>
    public Task<Uri> WaitForHttpsAddressAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        WaitAsync(_httpsAddress.Task, "开始监听 HTTPS", timeout, cancellationToken);

    /// <summary>等待宿主报告启动完成：全部监听都已建立。</summary>
    public async Task WaitForApplicationStartedAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        await WaitAsync(_started.Task.ContinueWith(_ => true, TaskScheduler.Default), "完成启动", timeout, cancellationToken);

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

    /// <summary>强制终止进程（Linux 上为 SIGKILL），模拟崩溃：进程没有机会执行任何停机逻辑。</summary>
    public async Task KillAsync(CancellationToken cancellationToken)
    {
        _process.Kill(entireProcessTree: false);
        await _process.WaitForExitAsync(cancellationToken);
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

    private async Task<T> WaitAsync<T>(Task<T> signal, string what, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            var waiting = signal.WaitAsync(timeoutSource.Token);
            var exited = _process.WaitForExitAsync(timeoutSource.Token);
            if (await Task.WhenAny(waiting, exited) == waiting)
            {
                return await waiting;
            }

            // WaitForExitAsync 完成时已读完重定向的输出，错误信息包含完整日志。
            await exited;
            throw new InvalidOperationException(
                $"宿主进程在{what}之前退出，退出码 {_process.ExitCode}。{Environment.NewLine}{Output}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"宿主在 {timeout.TotalSeconds} 秒内没有{what}。{Environment.NewLine}{Output}");
        }
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
            var address = new Uri(match.Groups["address"].Value);
            (address.Scheme == Uri.UriSchemeHttps ? _httpsAddress : _httpAddress).TrySetResult(address);
        }

        if (line.Contains("Application started", StringComparison.Ordinal))
        {
            _started.TrySetResult();
        }
    }

    [GeneratedRegex(@"Now listening on: (?<address>https?://127\.0\.0\.1:\d+)")]
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
