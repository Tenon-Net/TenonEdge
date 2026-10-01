using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using FluentModbus;
using NModbus;
using NModbus.IO;

namespace TenonEdge.IntegrationTests.Probes.Modbus;

/// <summary>参与比较的候选库。</summary>
public enum ModbusLibrary
{
    NModbus,
    FluentModbus,
}

/// <summary>
/// 用同一组用例驱动两个候选库的薄封装（T0-12 验证代码，不是正式适配器）：只做建立连接、设置超时与读取保持寄存器（功能码 03），
/// 不补任何库没有的行为，这样测试观察到的就是库本身的表现。寄存器按大端（Modbus 线上顺序）转为 ushort。
/// </summary>
internal abstract class ModbusProbeClient : IDisposable
{
    public abstract ModbusLibrary Library { get; }

    public static ModbusProbeClient Connect(ModbusLibrary library, ModbusFraming framing, IPEndPoint endpoint, ModbusProbeOptions options) =>
        library == ModbusLibrary.NModbus
            ? NModbusProbeClient.Connect(framing, endpoint, options)
            : FluentModbusProbeClient.Connect(framing, endpoint, options);

    /// <summary>同步读取：调用线程阻塞到应答、超时或连接被释放。</summary>
    public abstract ushort[] Read(byte unit, ushort address, ushort count);

    /// <summary>库提供的异步读取。</summary>
    public abstract Task<ushort[]> ReadAsync(byte unit, ushort address, ushort count, CancellationToken cancellationToken);

    public abstract void Dispose();
}

/// <summary>读取超时与 NModbus 的库内重试设置。</summary>
internal sealed record ModbusProbeOptions
{
    public static ModbusProbeOptions Default { get; } = new();

    /// <summary>读取超时；<see cref="Timeout.InfiniteTimeSpan"/> 表示不设置，即两库的默认值（无限）。</summary>
    public TimeSpan ReadTimeout { get; init; } = TimeSpan.FromMilliseconds(300);

    /// <summary>NModbus 的库内重试次数；为空时保持库的默认值（3 次）。FluentModbus 没有库内重试。</summary>
    public int? Retries { get; init; } = 0;

    /// <summary>NModbus：从站忙是否计入重试次数（默认 false，即无限重试）。</summary>
    public bool SlaveBusyUsesRetryCount { get; init; }

    /// <summary>NModbus：在多少个事务号之内的旧应答被跳过（默认 0，不跳过）。</summary>
    public uint RetryOnOldResponseThreshold { get; init; }
}

internal sealed class NModbusProbeClient : ModbusProbeClient
{
    private readonly IModbusMaster _master;

    private NModbusProbeClient(IModbusMaster master, TcpClient connection)
    {
        _master = master;
        Connection = connection;
    }

    public override ModbusLibrary Library => ModbusLibrary.NModbus;

    /// <summary>底层连接；NModbus 接收调用方建立的连接，适配层可以直接在它上面排空残留字节。</summary>
    public TcpClient Connection { get; }

    public IModbusTransport Transport => _master.Transport;

    public static NModbusProbeClient Connect(ModbusFraming framing, IPEndPoint endpoint, ModbusProbeOptions options)
    {
        var connection = new TcpClient { NoDelay = true };
        connection.Connect(endpoint);
        var factory = new ModbusFactory();

        // TCP 用 CreateMaster(TcpClient)；RTU over TCP 用 TcpClientAdapter 组合 RTU 传输（CreateMaster(Socket) 会选 RTU 且超时映射相反，见 S-07）。
        IModbusMaster master = framing == ModbusFraming.Tcp
            ? factory.CreateMaster(connection)
            : factory.CreateRtuMaster(new TcpClientAdapter(connection));
        if (options.ReadTimeout != Timeout.InfiniteTimeSpan)
        {
            master.Transport.ReadTimeout = (int)options.ReadTimeout.TotalMilliseconds;
            master.Transport.WriteTimeout = (int)options.ReadTimeout.TotalMilliseconds;
        }

        if (options.Retries is { } retries)
        {
            master.Transport.Retries = retries;
        }

        master.Transport.SlaveBusyUsesRetryCount = options.SlaveBusyUsesRetryCount;
        master.Transport.RetryOnOldResponseThreshold = options.RetryOnOldResponseThreshold;
        return new NModbusProbeClient(master, connection);
    }

    public override ushort[] Read(byte unit, ushort address, ushort count) => _master.ReadHoldingRegisters(unit, address, count);

    // NModbus 的异步方法不接受取消令牌，内部用 Task.Factory.StartNew 执行同步读取。
    public override Task<ushort[]> ReadAsync(byte unit, ushort address, ushort count, CancellationToken cancellationToken) =>
        _master.ReadHoldingRegistersAsync(unit, address, count);

    // 释放主站会依次释放传输、适配器和 TcpClient。
    public override void Dispose() => _master.Dispose();
}

internal sealed class FluentModbusProbeClient : ModbusProbeClient
{
    private readonly ModbusClient _client;

    private FluentModbusProbeClient(ModbusClient client)
    {
        _client = client;
    }

    public override ModbusLibrary Library => ModbusLibrary.FluentModbus;

    public static FluentModbusProbeClient Connect(ModbusFraming framing, IPEndPoint endpoint, ModbusProbeOptions options)
    {
        // 读写超时只在库自己建立连接时复制到网络流，必须在 Connect 之前设置。
        var timeout = options.ReadTimeout == Timeout.InfiniteTimeSpan ? Timeout.Infinite : (int)options.ReadTimeout.TotalMilliseconds;
        if (framing == ModbusFraming.Tcp)
        {
            var tcp = new ModbusTcpClient { ReadTimeout = timeout, WriteTimeout = timeout, ConnectTimeout = 5000 };
            tcp.Connect(endpoint, ModbusEndianness.BigEndian);
            return new FluentModbusProbeClient(tcp);
        }

        var rtu = new ModbusRtuOverTcpClient { ReadTimeout = timeout, WriteTimeout = timeout, ConnectTimeout = 5000 };
        rtu.Connect(endpoint, ModbusEndianness.BigEndian);
        return new FluentModbusProbeClient(rtu);
    }

    public override ushort[] Read(byte unit, ushort address, ushort count) => ToRegisters(_client.ReadHoldingRegisters(unit, address, count));

    public override async Task<ushort[]> ReadAsync(byte unit, ushort address, ushort count, CancellationToken cancellationToken) =>
        ToRegisters((await _client.ReadHoldingRegistersAsync(unit, address, count, cancellationToken)).Span);

    public override void Dispose() => ((IDisposable)_client).Dispose();

    private static ushort[] ToRegisters(ReadOnlySpan<byte> bytes)
    {
        var registers = new ushort[bytes.Length / 2];
        for (var i = 0; i < registers.Length; i++)
        {
            registers[i] = BinaryPrimitives.ReadUInt16BigEndian(bytes[(i * 2)..]);
        }

        return registers;
    }
}
