using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using MQTTnet;
using MQTTnet.Exceptions;
using TenonEdge.IntegrationTests.Infrastructure;

namespace TenonEdge.IntegrationTests.Probes.Mqtt;

/// <summary>
/// T0-11 的真实测试 Broker（验证代码）：用 Docker 运行 <see cref="Image"/>，配置见同目录的 mosquitto.conf。
/// 禁止匿名，用户名密码认证；明文 1883 与 TLS 8883 映射到 127.0.0.1 的随机端口；服务器证书由每个实例自己的测试 CA 签发。
/// 容器名带 tenonedge-probe-mqtt- 前缀并带标签 tenonedge.probe=mqtt，释放时删除；启动失败时先收集容器的输出写进异常，再删除。
/// 测试进程被强制结束而留下的容器，可用 <c>docker rm -fv $(docker ps -aq --filter label=tenonedge.probe=mqtt)</c> 清理。
/// 服务器私钥在容器复制之后即从临时目录删除。
/// 本机没有 Docker 或没有该镜像时，<see cref="UnavailableReason"/> 给出原因，测试据此跳过。
/// </summary>
internal sealed class MosquittoBroker : IAsyncDisposable
{
    public const string Image = "eclipse-mosquitto:2.1.2-alpine";
    public const string UserName = "probe-user";
    public const string Password = "probe-password-1";
    public const string ServerName = "localhost";

    private static readonly TimeSpan DockerTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(30);
    private static readonly Lazy<string?> Unavailable = new(DetectUnavailableReason);

    private readonly string _containerName;
    private readonly TemporaryDirectory _files;

    private MosquittoBroker(string containerName, TemporaryDirectory files, TestCertificateAuthority authority, string serverFingerprint)
    {
        _containerName = containerName;
        _files = files;
        Authority = authority;
        ServerCertificateSha256 = serverFingerprint;
    }

    /// <summary>不能运行真实 Broker 的原因；可以运行时为空。</summary>
    public static string? UnavailableReason => Unavailable.Value;

    public int Port { get; private set; }

    public int TlsPort { get; private set; }

    /// <summary>签发 Broker 服务器证书的测试 CA。</summary>
    public TestCertificateAuthority Authority { get; }

    /// <summary>Broker 服务器证书的 SHA-256 指纹（十六进制大写）。</summary>
    public string ServerCertificateSha256 { get; }

    public static async Task<MosquittoBroker> StartAsync(CancellationToken cancellationToken, bool persistence = false, bool stablePorts = false, int? maxQueuedMessages = null, int? maxQueuedBytes = null, bool deviceAcl = false)
    {
        if (maxQueuedMessages is <= 0)
        { throw new ArgumentOutOfRangeException(nameof(maxQueuedMessages)); }
        if (maxQueuedBytes is <= 0)
        { throw new ArgumentOutOfRangeException(nameof(maxQueuedBytes)); }
        var files = TemporaryDirectory.Create();
        var authority = TestCertificateAuthority.Create("TenonEdge Probe CA");
        var (certificate, key, fingerprint) = authority.IssueServerCertificate(ServerName);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Probes", "Mqtt", "mosquitto.conf"), Path.Combine(files.Path, "mosquitto.conf"));
        if (persistence)
        {
            // 不自动保存：分别实测正常停止的检查点与强杀前尚未保存的窗口。
            await File.AppendAllTextAsync(Path.Combine(files.Path, "mosquitto.conf"),
                "\npersistence true\npersistence_location /mosquitto/data/\nautosave_interval 0\n", cancellationToken);
        }
        if (maxQueuedMessages is { } maximum)
        {
            await File.AppendAllTextAsync(Path.Combine(files.Path, "mosquitto.conf"),
                $"\nmax_queued_messages {maximum.ToString(CultureInfo.InvariantCulture)}\n", cancellationToken);
        }
        if (maxQueuedBytes is { } maximumBytes)
        {
            await File.AppendAllTextAsync(Path.Combine(files.Path, "mosquitto.conf"),
                $"\nmax_queued_bytes {maximumBytes.ToString(CultureInfo.InvariantCulture)}\n", cancellationToken);
        }
        if (deviceAcl)
        {
            await File.AppendAllTextAsync(Path.Combine(files.Path, "mosquitto.conf"),
                "\nacl_file /mosquitto/config/acl\n", cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(files.Path, "acl"),
                DeviceAcl(denyPrivateTopic: false), cancellationToken);
        }
        await File.WriteAllTextAsync(Path.Combine(files.Path, "server.crt"), certificate, cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(files.Path, "server.key"), key, cancellationToken);

        var broker = new MosquittoBroker($"tenonedge-probe-mqtt-{Guid.NewGuid():N}", files, authority, fingerprint);
        try
        {
            // 以 root 把配置、证书复制进容器并生成密码文件，交给 mosquitto 用户；Mosquitto 2.1 要求密码文件归运行用户所有且不可被其他人读取。
            const string Setup =
                "set -e; if [ ! -f /mosquitto/config/server.key ]; then cp /probe/mosquitto.conf /probe/server.crt /probe/server.key /mosquitto/config/; "
                + $"mosquitto_passwd -b -c /mosquitto/config/passwd {UserName} {Password}; "
                + "if [ -f /probe/acl ]; then cp /probe/acl /mosquitto/config/acl; "
                + $"mosquitto_passwd -b /mosquitto/config/passwd sensor-a {Password}; mosquitto_passwd -b /mosquitto/config/passwd sensor-b {Password}; fi; fi; "
                + "chown mosquitto:mosquitto /mosquitto/data /mosquitto/config/*; chmod 0600 /mosquitto/config/passwd /mosquitto/config/server.key; "
                + "exec mosquitto -c /mosquitto/config/mosquitto.conf";
            var ports = stablePorts ? SelectHostPorts() : (Plain: "", Tls: "");
            await RunDockerAsync(
                [
                    "run", "-d", "--name", broker._containerName, "--label", "tenonedge.probe=mqtt",
                    "-p", $"127.0.0.1:{ports.Plain}:1883", "-p", $"127.0.0.1:{ports.Tls}:8883", "-v", $"{files.Path}:/probe:ro", Image, "sh", "-c", Setup,
                ],
                cancellationToken);
            broker.Port = await broker.MappedPortAsync(1883, cancellationToken);
            broker.TlsPort = await broker.MappedPortAsync(8883, cancellationToken);
            await broker.WaitUntilReadyAsync(cancellationToken);

            // 容器启动时已把证书与私钥复制进自己的文件系统，宿主机上的副本不再需要。
            File.Delete(Path.Combine(files.Path, "server.key"));
            return broker;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Broker 启动失败（例如配置错误）时，容器的输出是唯一的诊断信息，删除容器之前先取出来。
            string logs;
            try
            {
                logs = await RunDockerAsync(["logs", broker._containerName], CancellationToken.None);
            }
            catch (InvalidOperationException logsUnavailable)
            {
                logs = $"（无法读取容器输出：{logsUnavailable.Message}）";
            }

            await broker.DisposeAsync();
            throw new InvalidOperationException($"测试 Broker 没有启动：{exception.Message}{Environment.NewLine}容器输出：{Environment.NewLine}{logs}", exception);
        }
        catch
        {
            await broker.DisposeAsync();
            throw;
        }
    }

    /// <summary>暂停容器中的全部进程：TCP 连接保持，Broker 不再应答（docker pause）。</summary>
    public Task PauseAsync(CancellationToken cancellationToken) => RunDockerAsync(["pause", _containerName], cancellationToken);

    public Task UnpauseAsync(CancellationToken cancellationToken) => RunDockerAsync(["unpause", _containerName], cancellationToken);

    /// <summary>只允许首次快照；最终 mosquitto.db 的出现是完成临时文件写入并替换的屏障，不以信号发送成功冒充保存完成。</summary>
    internal async Task SaveFirstCheckpointAsync(CancellationToken cancellationToken)
    {
        await RunDockerAsync(["exec", _containerName, "sh", "-c", "test ! -e /mosquitto/data/mosquitto.db"], cancellationToken);
        await RunDockerAsync(["kill", "--signal", "USR1", _containerName], cancellationToken);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        while (true)
        {
            var state = await RunDockerAsync(["exec", _containerName, "sh", "-c",
                "if [ -s /mosquitto/data/mosquitto.db ]; then echo saved; else echo pending; fi"], deadline.Token);
            if (state.Trim() == "saved")
            { return; }
            await Task.Delay(TimeSpan.FromMilliseconds(25), deadline.Token);
        }
    }

    /// <summary>仅用于独占测试 Broker：保留容器文件系统，停止/强杀真实进程后重新启动。</summary>
    internal async Task RestartAsync(bool forceKill, CancellationToken cancellationToken)
    {
        await RunDockerAsync(forceKill ? ["kill", "--signal", "KILL", _containerName] : ["stop", "--time", "10", _containerName], cancellationToken);
        await RunDockerAsync(["start", _containerName], cancellationToken);
        var port = await MappedPortAsync(1883, cancellationToken);
        var tlsPort = await MappedPortAsync(8883, cancellationToken);
        if (port != Port || tlsPort != TlsPort)
        { throw new InvalidOperationException("测试 Broker 重启改变了映射端口，不能把不同接收方当作原持久会话验证。"); }
        await WaitUntilReadyAsync(cancellationToken);
    }

    private static string DeviceAcl(bool denyPrivateTopic) =>
        $"user {UserName}\ntopic read devices/#\n"
        + (denyPrivateTopic ? "topic deny devices/sensor-a/private\n" : string.Empty)
        + "user sensor-a\ntopic write devices/sensor-a/#\nuser sensor-b\ntopic write devices/sensor-b/#\n";

    /// <summary>独占 ACL 测试实例：原子替换 ACL 并 SIGHUP，不重启进程或现有客户端。</summary>
    internal async Task ReloadDeviceAclAsync(bool denyPrivateTopic, CancellationToken cancellationToken)
    {
        if (!File.Exists(Path.Combine(_files.Path, "acl")))
        { throw new InvalidOperationException("该测试 Broker 未启用设备 ACL。"); }
        var path = Path.Combine(_files.Path, "acl.next");
        await File.WriteAllTextAsync(path, DeviceAcl(denyPrivateTopic), cancellationToken);
        await RunDockerAsync(["cp", path, $"{_containerName}:/mosquitto/config/acl.next"], cancellationToken);
        await RunDockerAsync(["exec", _containerName, "sh", "-c",
            "chown mosquitto:mosquitto /mosquitto/config/acl.next; chmod 0600 /mosquitto/config/acl.next; mv /mosquitto/config/acl.next /mosquitto/config/acl"], cancellationToken);
        const string Reload = "Reloading config.";
        var before = (await LogsAsync(cancellationToken)).Split(Reload, StringSplitOptions.None).Length;
        await RunDockerAsync(["kill", "--signal", "HUP", _containerName], cancellationToken);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(StartupTimeout);
        while ((await LogsAsync(deadline.Token)).Split(Reload, StringSplitOptions.None).Length <= before)
        { await Task.Delay(TimeSpan.FromMilliseconds(25), deadline.Token); }
        // 重载日志在操作开始时输出；之后完成一次 MQTT 握手，确认主循环已继续处理网络。
        await WaitUntilReadyAsync(deadline.Token);
    }

    public Task<string> LogsAsync(CancellationToken cancellationToken) => RunDockerAsync(["logs", _containerName], cancellationToken);

    public async ValueTask DisposeAsync()
    {
        try
        {
            await RunDockerAsync(["rm", "-f", "-v", _containerName], CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
            // 容器没有创建成功时删除会失败，不影响清理临时文件。
        }

        Authority.Dispose();
        _files.Dispose();
    }

    private static (string Plain, string Tls) SelectHostPorts()
    {
        // Docker 的随机映射会在 restart 时换端口。独占重启测试显式映射两个空闲端口。
        // 释放监听器到 Docker 绑定之间若被其他进程抢占，启动明确失败，不悄悄更换 Broker 身份。
        using var plain = new TcpListener(IPAddress.Loopback, 0);
        using var tls = new TcpListener(IPAddress.Loopback, 0);
        plain.Start();
        tls.Start();
        return (((IPEndPoint)plain.LocalEndpoint).Port.ToString(CultureInfo.InvariantCulture),
            ((IPEndPoint)tls.LocalEndpoint).Port.ToString(CultureInfo.InvariantCulture));
    }

    private static string? DetectUnavailableReason()
    {
        try
        {
            using var timeout = new CancellationTokenSource(DockerTimeout);
            RunDockerAsync(["image", "inspect", "--format", "{{.Id}}", Image], timeout.Token).GetAwaiter().GetResult();
            return null;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or OperationCanceledException)
        {
            return $"需要本机 Docker 与镜像 {Image}（T0-11/T1-04 真实 Broker 验证）：{exception.Message}";
        }
    }

    private static async Task<string> RunDockerAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("docker")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动 docker 命令。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(DockerTimeout);
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            // 超时或取消时结束 docker 命令，不留下仍在运行的子进程。
            process.Kill(entireProcessTree: true);
            throw;
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"docker {string.Join(' ', arguments.Take(2))} 失败（退出码 {process.ExitCode}）：{(await error).Trim()}");
        }

        return await output;
    }

    private async Task<int> MappedPortAsync(int containerPort, CancellationToken cancellationToken)
    {
        // 输出形如 127.0.0.1:49153。
        var mapping = (await RunDockerAsync(["port", _containerName, $"{containerPort}/tcp"], cancellationToken)).Trim().Split('\n')[0];
        return int.Parse(mapping[(mapping.LastIndexOf(':') + 1)..], NumberStyles.None, CultureInfo.InvariantCulture);
    }

    /// <summary>Docker 的端口转发在容器内进程监听之前就接受 TCP 连接，所以用完整的 MQTT 连接判断就绪。</summary>
    private async Task WaitUntilReadyAsync(CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(StartupTimeout);
        while (true)
        {
            using var client = new MqttClientFactory().CreateMqttClient();
            try
            {
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                attempt.CancelAfter(TimeSpan.FromSeconds(2));
                var result = await client.ConnectAsync(MqttProbe.Options(this, "probe-readiness"), attempt.Token);
                if (result.ResultCode == MqttClientConnectResultCode.Success)
                {
                    await client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build(), deadline.Token);
                    return;
                }
            }
            catch (Exception exception) when ((exception is MqttCommunicationException or SocketException or OperationCanceledException) && !deadline.IsCancellationRequested)
            {
                // Broker 尚未就绪，稍后重试。
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200), deadline.Token);
        }
    }
}
