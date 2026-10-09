using System.Buffers.Binary;
using NModbus;
using NModbus.IO;
using NModbus.Utility;

namespace TenonEdge.Adapters.Modbus;

internal enum ModbusFrameFormat { Tcp, RtuOverTcp }

internal sealed class ModbusDeviceException(byte code) : Exception("Modbus 从站返回异常应答。")
{
    internal byte Code { get; } = code;
}

/// <summary>只开放 FC 01/02/03/04；在连接的 ModbusRequestExecutor 上串行调用。
/// stream 必须由统一出站连接器取得；本类型不解析 DNS、不重连、不启动线程。
/// TCP 超时保留半帧以便下一请求跳过旧应答；RTU 超时后调用者必须先静默排空。
/// 取消只关闭捕获的同代 stream，再等待执行器退出。</summary>
internal sealed class ModbusReadClient : IDisposable
{
    private readonly Stream _stream;
    private readonly ModbusFrameFormat _format;
    private readonly ModbusReadStream _resource;
    private readonly IModbusMaster _master;

    internal ModbusReadClient(Stream stream, ModbusFrameFormat format, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(time);
        if (!Enum.IsDefined(format))
        { throw new ArgumentOutOfRangeException(nameof(format)); }
        if (!stream.CanRead || !stream.CanWrite || !stream.CanTimeout)
        { throw new ArgumentException("Modbus 需要支持同步读写超时的双向流。", nameof(stream)); }
        _stream = stream;
        _format = format;
        _resource = new ModbusReadStream(stream, format, time);
        var factory = new ModbusFactory();
        _master = format == ModbusFrameFormat.Tcp ? factory.CreateIpMaster(_resource) : factory.CreateRtuMaster(_resource);
        _master.Transport.Retries = 0;
        _master.Transport.SlaveBusyUsesRetryCount = true;
        _master.Transport.WaitToRetryMilliseconds = 0;
    }

    internal bool NeedsNewConnection => _resource.NeedsNewConnection;

    internal ushort[] ReadRegisters(byte unit, byte function, ushort address, ushort count, TimeSpan timeout)
    {
        Validate(unit, function, address, count, false, timeout);
        return function == 3 ? _master.ReadHoldingRegisters(unit, address, count) : _master.ReadInputRegisters(unit, address, count);
    }

    internal bool[] ReadBits(byte unit, byte function, ushort address, ushort count, TimeSpan timeout)
    {
        Validate(unit, function, address, count, true, timeout);
        return function == 1 ? _master.ReadCoils(unit, address, count) : _master.ReadInputs(unit, address, count);
    }

    private void Validate(byte unit, byte function, ushort address, ushort count, bool bits, TimeSpan timeout)
    {
        if (_format == ModbusFrameFormat.RtuOverTcp && unit is < 1 or > 247)
        { throw new ArgumentOutOfRangeException(nameof(unit)); }
        if (bits ? function is not (1 or 2) : function is not (3 or 4))
        { throw new ArgumentOutOfRangeException(nameof(function)); }
        if (count == 0 || count > (bits ? 2000 : 125) || (int)address + count > 65536)
        { throw new ArgumentOutOfRangeException(nameof(count)); }
        _resource.Begin(timeout);
    }

    internal void Abort() => _stream.Dispose();
    public void Dispose() => _master.Dispose();
}

/// <summary>库前的有界帧门禁；不复制第三方实现。完整应答校验后才交给 NModbus 解码。
/// ACK(05)/Busy(06) 同其他异常码立即返回，不进入库的等待/重发循环。
/// 一代 TCP 连接的事务号不重复使用，到回绕前要求换连接，避免旧帧与新事务同号。</summary>
internal sealed class ModbusReadStream(Stream stream, ModbusFrameFormat format, TimeProvider time) : IStreamResource
{
    private readonly byte[] _frame = new byte[260];
    private int _length;
    private int _offset;
    private int _received;
    private long _started;
    private TimeSpan _timeout;
    private ushort _transaction;
    private byte _unit;
    private byte _function;
    private int _dataBytes;

    public int InfiniteTimeout => Timeout.Infinite;
    public int ReadTimeout { get; set; } = 1000;
    public int WriteTimeout { get; set; } = 1000;
    internal bool NeedsNewConnection { get; private set; }

    internal void Begin(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > int.MaxValue)
        { throw new ArgumentOutOfRangeException(nameof(timeout)); }
        if (NeedsNewConnection)
        { throw new IOException("Modbus TCP 事务号已耗尽，必须更换连接。"); }
        _started = time.GetTimestamp();
        _timeout = timeout;
        _length = 0;
        _offset = 0;
        if (format == ModbusFrameFormat.RtuOverTcp)
        { _received = 0; }
    }

    public void Write(byte[] buffer, int offset, int count)
    {
        var request = buffer.AsSpan(offset, count);
        var start = format == ModbusFrameFormat.Tcp ? 6 : 0;
        if (count != (start == 6 ? 12 : 8))
        { throw new IOException("不支持的 Modbus 请求帧。"); }
        if (start == 6)
        {
            var next = BinaryPrimitives.ReadUInt16BigEndian(request);
            if (next == 0 || next <= _transaction)
            { NeedsNewConnection = true; throw new IOException("Modbus TCP 事务号不得在同一连接回绕。"); }
            _transaction = next;
            if (next == ushort.MaxValue)
            { NeedsNewConnection = true; }
        }
        _unit = request[start];
        _function = request[start + 1];
        if (_function is < 1 or > 4)
        { throw new IOException("只允许 Modbus 读取功能码。"); }
        var quantity = BinaryPrimitives.ReadUInt16BigEndian(request[(start + 4)..]);
        _dataBytes = _function <= 2 ? (quantity + 7) / 8 : quantity * 2;
        stream.WriteTimeout = RemainingMilliseconds();
        stream.Write(request);
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        var destination = buffer.AsSpan(offset, count);
        if (count == 0)
        { return 0; }
        if (_offset == _length)
        { LoadResponse(); }
        var copied = Math.Min(count, _length - _offset);
        _frame.AsSpan(_offset, copied).CopyTo(destination);
        _offset += copied;
        return copied;
    }

    private void LoadResponse()
    {
        while (true)
        {
            var start = format == ModbusFrameFormat.Tcp ? 6 : 0;
            if (start == 6)
            {
                Fill(6);
                var length = BinaryPrimitives.ReadUInt16BigEndian(_frame.AsSpan(4));
                if (BinaryPrimitives.ReadUInt16BigEndian(_frame.AsSpan(2)) != 0 || length is < 3 or > 254)
                { NeedsNewConnection = true; throw new IOException("Modbus TCP 协议号或长度无效。"); }
                Fill(6 + length);
                _received = 0;
                var transaction = BinaryPrimitives.ReadUInt16BigEndian(_frame);
                if (transaction != _transaction)
                {
                    if (transaction > 0 && transaction < _transaction)
                    { continue; }
                    throw new IOException("Modbus TCP 应答事务号无效。");
                }
                _length = 6 + length;
            }
            else
            {
                Fill(3);
                _length = (_frame[1] & 0x80) != 0 ? 5 : 5 + _frame[2];
                Fill(_length);
                _received = 0;
                var crc = ModbusUtility.CalculateCrc(_frame.AsSpan(0, _length - 2).ToArray());
                if (!_frame.AsSpan(_length - 2, 2).SequenceEqual(crc))
                { throw new IOException("Modbus RTU CRC 校验失败。"); }
            }
            if (_frame[start] != _unit || (_frame[start + 1] & 0x7f) != _function)
            { throw new IOException("Modbus 应答从站或功能码不符。"); }
            if ((_frame[start + 1] & 0x80) != 0)
            {
                if (_length != (start == 6 ? 9 : 5))
                { throw new IOException("Modbus 异常应答长度无效。"); }
                throw new ModbusDeviceException(_frame[start + 2]);
            }
            if (_frame[start + 2] != _dataBytes || _length != start + 3 + _dataBytes + (start == 6 ? 0 : 2))
            { throw new IOException("Modbus 应答字节数不符。"); }
            _offset = 0;
            return;
        }
    }

    private void Fill(int total)
    {
        while (_received < total)
        {
            stream.ReadTimeout = RemainingMilliseconds();
            var read = stream.Read(_frame, _received, total - _received);
            if (read == 0)
            { throw new EndOfStreamException("Modbus 连接已关闭。"); }
            // TCP 读取超时时保留已消费字节，不把下一帧/旧帧的剩余部分误当作新 MBAP。
            _received += read;
        }
        _ = RemainingMilliseconds();
    }

    private int RemainingMilliseconds()
    {
        var remaining = _timeout - time.GetElapsedTime(_started);
        if (remaining <= TimeSpan.Zero)
        { throw new TimeoutException("Modbus 请求总期限已到。"); }
        return Math.Max(1, (int)Math.Ceiling(remaining.TotalMilliseconds));
    }

    // RTU 超时隔离由拥有连接的调度层显式执行；Flush 不等于排空。
    public void DiscardInBuffer() { }
    public void Dispose() => stream.Dispose();
}
