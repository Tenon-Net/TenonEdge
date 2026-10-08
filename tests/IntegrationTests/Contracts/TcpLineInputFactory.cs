using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Abstractions.Plans;

namespace TenonEdge.IntegrationTests.Contracts;

/// <summary>
/// 测试适配器（替身），不是正式适配器：连接一个 TCP“设备”，每读到一行十进制整数生成一条统一消息，字段名为 value。
/// 用于验证运行单元契约与运行时：构建只做本地计算，不连接、不采集；运行后才连接；停止时由单元释放连接，打断不响应取消的阻塞读取。
/// 业务标识为 deviceKey/inputKey，配置示例：{"address":"127.0.0.1","port":5020}。
/// 地址只接受 IP 字面量：解析主机名属于网络 I/O，不能放在构建阶段。
/// </summary>
internal sealed class TcpLineInputFactory : IInputUnitFactory
{
    public const string UnitKind = "input.testline";

    public string Kind => UnitKind;

    public IRuntimeUnit Build(RuntimeUnitPlan unit, InputUnitContext context)
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

        return new TcpLineInput(unit.Id, new IPEndPoint(ipAddress, portNumber), context);
    }
}

/// <summary>
/// <see cref="TcpLineInputFactory"/> 构建的运行单元。设备关闭连接或连接失败时抛出异常，由监督者记录并按退避重启。
/// 停止：取消令牌的回调释放套接字，打断不响应取消的读取（总设计 §8.1）。
/// </summary>
internal sealed class TcpLineInput(RuntimeUnitId id, IPEndPoint endpoint, InputUnitContext context) : IRuntimeUnit
{
    public const string ValueField = "value";

    public RuntimeUnitId Id => id;

    public async Task RunAsync(CancellationToken stoppingToken)
    {
        if (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        using var socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        await using var release = stoppingToken.Register(socket.Dispose);
        await socket.ConnectAsync(endpoint, stoppingToken);
        using var stream = new NetworkStream(socket, ownsSocket: false);
        using var reader = new StreamReader(stream, Encoding.ASCII);
        while (!stoppingToken.IsCancellationRequested)
        {
            // 故意不传取消令牌，模拟不支持取消的协议库：只有释放连接才能打断这次读取。
            var line = await reader.ReadLineAsync(CancellationToken.None)
                ?? throw new IOException("设备关闭了连接。");
            await context.Messages.WriteAsync(CreateMessage(line), stoppingToken);
            context.Reads.ReadCompleted(context.Time.GetUtcNow(), 0);
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private EdgeMessage CreateMessage(string line)
    {
        var receivedAt = context.Time.GetUtcNow();
        var parsed = long.TryParse(line, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value);
        return new EdgeMessage(
            Guid.CreateVersion7(receivedAt),
            context.InputKey,
            context.DeviceKey,
            receivedAt,
            context.GatewayTimeSource,
            receivedAt,
            parsed ? [new(ValueField, FieldValue.FromInt64(value))] : [],
            parsed ? null : [new(ValueField, QualityCode.ConversionFailed)]);
    }
}
