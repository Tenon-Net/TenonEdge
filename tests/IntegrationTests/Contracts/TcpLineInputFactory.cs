using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Abstractions.Plans;

namespace TenonEdge.IntegrationTests.Contracts;

/// <summary>
/// T0-10 的测试适配器（替身），不是正式适配器：连接一个 TCP“设备”，每读到一行十进制整数生成一条统一消息，字段名为 value。
/// 用于验证运行单元契约：构建只做本地计算，不连接、不采集；启动后才连接；停止时由单元释放连接，打断不响应取消的阻塞读取。
/// T1 的运行时测试可以继续使用它。配置示例：{"address":"127.0.0.1","port":5020,"deviceKey":"meter-1"}。
/// 地址只接受 IP 字面量：解析主机名属于网络 I/O，不能放在构建阶段。
/// </summary>
internal sealed class TcpLineInputFactory : IRuntimeUnitFactory
{
    public const string UnitKind = "input.testline";

    public string Kind => UnitKind;

    public IRuntimeUnit Build(RuntimeUnitPlan unit, RuntimeUnitContext context)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(context);
        if (unit.Id.Kind != Kind)
        {
            throw new RuntimeUnitBuildException(unit.Id, $"单元种类应为 {Kind}。");
        }

        var configuration = unit.Configuration;
        if (!configuration.TryGetProperty("address", out var address)
            || address.ValueKind != JsonValueKind.String
            || !IPAddress.TryParse(address.GetString(), out var ipAddress))
        {
            throw new RuntimeUnitBuildException(unit.Id, "address 须为 IP 地址。");
        }

        if (!configuration.TryGetProperty("port", out var port)
            || port.ValueKind != JsonValueKind.Number
            || !port.TryGetInt32(out var portNumber)
            || portNumber is < 1 or > 65535)
        {
            throw new RuntimeUnitBuildException(unit.Id, "port 须为 1—65535 的整数。");
        }

        if (!configuration.TryGetProperty("deviceKey", out var deviceKey)
            || deviceKey.ValueKind != JsonValueKind.String
            || string.IsNullOrEmpty(deviceKey.GetString()))
        {
            throw new RuntimeUnitBuildException(unit.Id, "deviceKey 不能为空。");
        }

        return new TcpLineInput(unit.Id, new IPEndPoint(ipAddress, portNumber), deviceKey.GetString()!, context);
    }
}

/// <summary>
/// <see cref="TcpLineInputFactory"/> 构建的运行单元。循环中的错误只记录在 <see cref="LastError"/>，
/// 按退避重启由 T1 的监督者负责，测试适配器不重连。调用约定（与 <see cref="IRuntimeUnit"/> 一致）：
/// <list type="bullet">
/// <item>StartAsync 至多调用一次；StopAsync 之后（包括在启动之前就请求停止）不能再启动。</item>
/// <item>StopAsync 的期限到时抛出取消异常，单元仍视为存活；DisposeAsync 最多等待 10 秒，到时同样抛出取消异常。</item>
/// </list>
/// </summary>
internal sealed class TcpLineInput(RuntimeUnitId id, IPEndPoint endpoint, string deviceKey, RuntimeUnitContext context) : IRuntimeUnit
{
    public const string ValueField = "value";

    private static readonly TimeSpan DisposeTimeout = TimeSpan.FromSeconds(10);

    private readonly CancellationTokenSource _stopping = new();
    private readonly Lock _lock = new();
    private Task? _loop;
    private Socket? _socket;
    private bool _stopRequested;
    private bool _disposed;

    public RuntimeUnitId Id => id;

    /// <summary>循环中最近一次错误；停止过程中因释放连接引起的异常属于正常退出，不记录。</summary>
    public Exception? LastError { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_loop is not null)
            {
                throw new InvalidOperationException("运行单元只能启动一次。");
            }

            if (_stopRequested)
            {
                throw new InvalidOperationException("运行单元已请求停止，不能再启动。");
            }

            _loop = RunAsync(_stopping.Token);
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task? loop;
        lock (_lock)
        {
            _stopRequested = true;
            loop = _loop;
        }

        if (loop is null)
        {
            return;
        }

        await _stopping.CancelAsync();
        lock (_lock)
        {
            // 读取不响应取消：释放连接来打断（总设计 §8.1）。
            _socket?.Dispose();
        }

        await loop.WaitAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        try
        {
            using var timeout = new CancellationTokenSource(DisposeTimeout);
            await StopAsync(timeout.Token);
        }
        finally
        {
            lock (_lock)
            {
                _socket?.Dispose();
            }

            _stopping.Dispose();
        }
    }

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        // 让 StartAsync 立即返回：连接在循环中建立，不阻塞启动。
        await Task.Yield();
        try
        {
            var socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            lock (_lock)
            {
                if (stoppingToken.IsCancellationRequested)
                {
                    socket.Dispose();
                    return;
                }

                _socket = socket;
            }

            await socket.ConnectAsync(endpoint, stoppingToken);
            using var stream = new NetworkStream(socket, ownsSocket: false);
            using var reader = new StreamReader(stream, Encoding.ASCII);
            while (!stoppingToken.IsCancellationRequested)
            {
                // 故意不传取消令牌，模拟不支持取消的协议库：只有释放连接才能打断这次读取。
                var line = await reader.ReadLineAsync(CancellationToken.None);
                if (line is null)
                {
                    LastError = new IOException("设备关闭了连接。");
                    return;
                }

                await context.Messages.WriteAsync(CreateMessage(line), stoppingToken);
            }
        }
        catch (Exception) when (stoppingToken.IsCancellationRequested)
        {
            // 停止过程中释放连接或取消等待而引起的异常属于正常退出。
        }
        catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException or ChannelClosedException)
        {
            LastError = exception;
        }
    }

    private EdgeMessage CreateMessage(string line)
    {
        var receivedAt = context.Time.GetUtcNow();
        var parsed = long.TryParse(line, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value);
        return new EdgeMessage(
            Guid.CreateVersion7(receivedAt),
            Id.Key,
            deviceKey,
            receivedAt,
            TimeSource.Gateway,
            receivedAt,
            parsed ? [new(ValueField, FieldValue.FromInt64(value))] : [],
            parsed ? null : [new(ValueField, QualityCode.ConversionFailed)]);
    }
}
