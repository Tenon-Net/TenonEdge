using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace TenonEdge.IntegrationTests.Probes.Modbus;

/// <summary>帧格式：Modbus TCP（MBAP 头）或 RTU over TCP（带 CRC 的 RTU 帧，经透传串口服务器）。</summary>
public enum ModbusFraming
{
    Tcp,
    RtuOverTcp,
}

/// <summary>模拟从站收到的一个读取请求。<see cref="TransactionId"/> 只对 Modbus TCP 有意义。</summary>
internal sealed record SlaveRequest(int Sequence, ushort TransactionId, byte Unit, byte FunctionCode, ushort Address, ushort Quantity);

/// <summary>模拟从站对一个请求的处理方式；默认立即正常应答。</summary>
internal sealed record SlaveReply
{
    public static SlaveReply Normal { get; } = new();

    /// <summary>应答前等待的时间，用于制造超时与迟到应答。</summary>
    public TimeSpan Delay { get; init; }

    /// <summary>不应答。</summary>
    public bool Silent { get; init; }

    /// <summary>以异常应答（功能码加 0x80），例如 6 表示从站忙。</summary>
    public byte? ExceptionCode { get; init; }

    /// <summary>Modbus TCP：应答事务号相对请求事务号的偏移，-1 即“较旧事务号的应答”。</summary>
    public int TransactionIdOffset { get; init; }

    /// <summary>应答中使用的从站地址（TCP 为 MBAP 的单元标识）；为空时与请求相同。</summary>
    public byte? Unit { get; init; }

    /// <summary>RTU：把应答的 CRC 改错。</summary>
    public bool CorruptCrc { get; init; }

    /// <summary>读到请求后直接关闭连接，不应答。</summary>
    public bool CloseConnection { get; init; }

    /// <summary>把同一个应答连发两遍（一次写出），模拟重复的应答帧。</summary>
    public bool Duplicate { get; init; }

    /// <summary>应答之后再等这么久才读取下一个请求，使迟到应答与下一个应答分两次到达。</summary>
    public TimeSpan PauseAfter { get; init; }
}

/// <summary>
/// T0-12 的进程内模拟从站（验证代码，不是正式适配器）：监听 127.0.0.1 的随机端口，按所选帧格式处理功能码 01/02/03/04 的读取请求，
/// 一个连接上的请求按到达顺序逐个处理（与串口服务器后的串行总线一致）。位输入按地址偶数为 true、奇数为 false；寄存器值固定为 0x1000 加地址，便于从结果看出读到的是哪个地址。
/// 每个请求按 <see cref="SlaveReply"/> 注入延迟、不应答、异常应答、较旧事务号、错误从站地址、CRC 错误或关闭连接。
/// </summary>
internal sealed class ModbusTestSlave : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly ModbusFraming _framing;
    private readonly Func<SlaveRequest, SlaveReply> _behavior;
    private readonly CancellationTokenSource _stopping = new();
    private readonly ConcurrentQueue<SlaveRequest> _requests = new();
    private readonly ConcurrentBag<TcpClient> _connections = [];
    private readonly ConcurrentBag<Task> _sessions = [];
    private readonly Task _acceptLoop;
    private int _requestCount;
    private int _responseCount;
    private int _connectionCount;

    private ModbusTestSlave(ModbusFraming framing, Func<SlaveRequest, SlaveReply> behavior)
    {
        _framing = framing;
        _behavior = behavior;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        // 模拟从站是独立对端，不能捕获客户端测试的同步上下文。
        // 否则同步 Read 占住上下文时，从站的 Accept/Read continuation 无法运行，制造假超时。
        _acceptLoop = Task.Run(() => AcceptAsync(_stopping.Token));
    }

    public IPEndPoint Endpoint => (IPEndPoint)_listener.LocalEndpoint;

    /// <summary>已收到的请求（按到达顺序）。</summary>
    public IReadOnlyList<SlaveRequest> Requests => [.. _requests];

    public int RequestCount => Volatile.Read(ref _requestCount);

    /// <summary>已写出的应答数。</summary>
    public int ResponseCount => Volatile.Read(ref _responseCount);

    public int ConnectionCount => Volatile.Read(ref _connectionCount);

    public static ModbusTestSlave Start(ModbusFraming framing, Func<SlaveRequest, SlaveReply>? behavior = null) =>
        new(framing, behavior ?? (_ => SlaveReply.Normal));

    /// <summary>寄存器的已知值。</summary>
    public static ushort RegisterValue(int address) => (ushort)(0x1000 + address);

    public static ushort[] RegisterValues(int address, int count) =>
        [.. Enumerable.Range(address, count).Select(RegisterValue)];

    /// <summary>等到从站收到至少 <paramref name="count"/> 个请求。</summary>
    public Task WaitForRequestsAsync(int count, TimeSpan timeout, CancellationToken cancellationToken) =>
        WaitUntilAsync(() => RequestCount >= count, timeout, cancellationToken);

    /// <summary>等到从站写出至少 <paramref name="count"/> 个应答。</summary>
    public Task WaitForResponsesAsync(int count, TimeSpan timeout, CancellationToken cancellationToken) =>
        WaitUntilAsync(() => ResponseCount >= count, timeout, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        _listener.Stop();
        foreach (var connection in _connections)
        {
            connection.Dispose();
        }

        try
        {
            await Task.WhenAll([_acceptLoop, .. _sessions]);
        }
        catch (Exception exception) when (exception is OperationCanceledException or SocketException or IOException or ObjectDisposedException)
        {
            // 停止时套接字被关闭，循环以这些异常结束属于正常情况。
        }

        _listener.Dispose();
        _stopping.Dispose();
    }

    /// <summary>Modbus RTU 的 CRC-16（多项式 0xA001，初值 0xFFFF），低字节在前发送。</summary>
    public static ushort Crc16(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (ushort)((crc >> 1) ^ 0xA001) : (ushort)(crc >> 1);
            }
        }

        return crc;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        while (!condition())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), deadline.Token);
        }
    }

    private async Task AcceptAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var client = await _listener.AcceptTcpClientAsync(cancellationToken);
            client.NoDelay = true;
            Interlocked.Increment(ref _connectionCount);
            _connections.Add(client);
            _sessions.Add(ServeAsync(client, cancellationToken));
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            var stream = client.GetStream();
            while (!cancellationToken.IsCancellationRequested)
            {
                var request = _framing == ModbusFraming.Tcp
                    ? await ReadTcpRequestAsync(stream, cancellationToken)
                    : await ReadRtuRequestAsync(stream, cancellationToken);
                if (request is null)
                {
                    return;
                }

                request = request with { Sequence = Interlocked.Increment(ref _requestCount) };
                _requests.Enqueue(request);
                var reply = _behavior(request);
                if (reply.CloseConnection)
                {
                    return;
                }

                if (reply.Silent)
                {
                    continue;
                }

                if (reply.Delay > TimeSpan.Zero)
                {
                    await Task.Delay(reply.Delay, cancellationToken);
                }

                var response = BuildResponse(request, reply);
                await stream.WriteAsync(reply.Duplicate ? [.. response, .. response] : response, cancellationToken);
                Interlocked.Increment(ref _responseCount);
                if (reply.PauseAfter > TimeSpan.Zero)
                {
                    await Task.Delay(reply.PauseAfter, cancellationToken);
                }
            }
        }
    }

    private static async Task<SlaveRequest?> ReadTcpRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var header = new byte[7];
        if (!await TryReadExactlyAsync(stream, header, cancellationToken))
        {
            return null;
        }

        var length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4));
        var pdu = new byte[length - 1];
        if (!await TryReadExactlyAsync(stream, pdu, cancellationToken))
        {
            return null;
        }

        return new SlaveRequest(
            0,
            BinaryPrimitives.ReadUInt16BigEndian(header),
            header[6],
            pdu[0],
            BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(1)),
            BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(3)));
    }

    private static async Task<SlaveRequest?> ReadRtuRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        // 功能码 01—04 的请求固定 8 个字节：从站地址、功能码、起始地址、数量、CRC。
        var frame = new byte[8];
        if (!await TryReadExactlyAsync(stream, frame, cancellationToken))
        {
            return null;
        }

        if (Crc16(frame.AsSpan(0, 6)) != BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(6)))
        {
            throw new InvalidDataException("模拟从站收到 CRC 错误的请求。");
        }

        return new SlaveRequest(
            0,
            0,
            frame[0],
            frame[1],
            BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(2)),
            BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(4)));
    }

    private static async Task<bool> TryReadExactlyAsync(NetworkStream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        try
        {
            await stream.ReadExactlyAsync(buffer, cancellationToken);
            return true;
        }
        catch (Exception exception) when (exception is EndOfStreamException or IOException or ObjectDisposedException)
        {
            return false;
        }
    }

    private byte[] BuildResponse(SlaveRequest request, SlaveReply reply)
    {
        byte[] pdu;
        if (reply.ExceptionCode is { } exceptionCode)
        {
            pdu = [(byte)(request.FunctionCode | 0x80), exceptionCode];
        }
        else if (request.FunctionCode is 1 or 2)
        {
            pdu = new byte[2 + ((request.Quantity + 7) / 8)];
            pdu[0] = request.FunctionCode;
            pdu[1] = (byte)(pdu.Length - 2);
            for (var i = 0; i < request.Quantity; i++)
            {
                if ((request.Address + i) % 2 == 0)
                { pdu[2 + (i / 8)] |= (byte)(1 << (i % 8)); }
            }
        }
        else
        {
            pdu = new byte[2 + (request.Quantity * 2)];
            pdu[0] = request.FunctionCode;
            pdu[1] = (byte)(request.Quantity * 2);
            for (var i = 0; i < request.Quantity; i++)
            {
                BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(2 + (i * 2)), RegisterValue(request.Address + i));
            }
        }

        var unit = reply.Unit ?? request.Unit;
        if (_framing == ModbusFraming.Tcp)
        {
            var frame = new byte[7 + pdu.Length];
            BinaryPrimitives.WriteUInt16BigEndian(frame, unchecked((ushort)(request.TransactionId + reply.TransactionIdOffset)));
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2), 0);
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4), (ushort)(pdu.Length + 1));
            frame[6] = unit;
            pdu.CopyTo(frame.AsSpan(7));
            return frame;
        }

        var rtu = new byte[1 + pdu.Length + 2];
        rtu[0] = unit;
        pdu.CopyTo(rtu.AsSpan(1));
        var crc = Crc16(rtu.AsSpan(0, rtu.Length - 2));
        if (reply.CorruptCrc)
        {
            crc ^= 0x0101;
        }

        BinaryPrimitives.WriteUInt16LittleEndian(rtu.AsSpan(rtu.Length - 2), crc);
        return rtu;
    }
}
