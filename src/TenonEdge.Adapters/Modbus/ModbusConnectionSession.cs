using System.Net.Sockets;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Adapters.Diagnostics;

namespace TenonEdge.Adapters.Modbus;

internal sealed record ModbusReadResult(ModbusRequestOutcome Outcome, bool WasRequested,
    ushort[]? Registers = null, bool[]? Bits = null, byte? ExceptionCode = null, bool ConnectionAttempted = false);

/// <summary>一个物理连接的会话；所有读取须经同一 IConnectionLease 串行准入。
/// 拥有一条同步 I/O 工作线程，最后租约等待实际 I/O 完成后才销毁。构造不联网。
/// UnitId 整体周期失败/离线退避属于采集调度层，不从跳过的请求推断设备超时。</summary>
internal sealed class ModbusConnectionSession : IAsyncDisposable, IConnectionObservationSource
{
    private readonly ModbusConnectionSettings _settings;
    private HashSet<byte>? _units;
    private ModbusConnectionHealth? _health;
    private readonly IOutboundConnector _connector;
    private readonly TimeProvider _time;
    private readonly ConnectionObservationTracker _observation;
    public ConnectionObservation Observation => _observation.Snapshot;
    private readonly ModbusRequestExecutor _executor;
    private Stream? _stream;
    private ModbusReadClient? _client;
    private long? _lastFinished;
    private long? _retryStarted;
    private TimeSpan _retryDelay;
    private int _reconnectSeconds = 1;
    private bool _disposed;

    internal ModbusConnectionSession(ModbusConnectionSettings settings, IEnumerable<byte>? enabledUnits,
        IOutboundConnector connector, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(connector);
        ArgumentNullException.ThrowIfNull(time);
        _settings = settings.Validate();
        if (enabledUnits is not null)
        { ConfigureUnits(enabledUnits); }
        _connector = connector;
        _time = time;
        _observation = new(time);
        _executor = new ModbusRequestExecutor();
    }

    // 测试先创建的会话尚无采集成员；运行时必须在同一串行租约中一次性绑定完整集合。
    // 后续重复绑定不清空健康历史，集合不同则拒绝，不能静默合并两个运行计划。
    internal void ConfigureUnits(IEnumerable<byte> enabledUnits)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(enabledUnits);
        var units = enabledUnits.ToHashSet();
        if (units.Count == 0 || (_settings.Format == ModbusFrameFormat.RtuOverTcp && units.Any(unit => unit is < 1 or > 247)))
        { throw new ArgumentException("Modbus 启用从站集合无效。", nameof(enabledUnits)); }
        if (_units is not null)
        {
            if (!_units.SetEquals(units))
            { throw new InvalidOperationException("共享会话的运行时从站集合不一致。"); }
            return;
        }
        _units = units;
        _health = new ModbusConnectionHealth(units);
    }

    // 调用者必须持有共享串行租约；只建立传输，不发送 Modbus 报文。
    internal async Task<ModbusConnectionTestResult> TestConnectionAsync(CancellationToken token, TimeSpan? remaining = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        token.ThrowIfCancellationRequested();
        var started = _time.GetTimestamp();
        var trace = new ConnectionTestTrace(_time, false);
        if (_client is not null && !_client.NeedsNewConnection)
        { return Result("connection.reused", true, true); }
        if (_retryStarted is { } failedAt && _time.GetElapsedTime(failedAt) < _retryDelay)
        { return Result("connection.backoff", false); }
        if (_client?.NeedsNewConnection == true)
        { Reset(backoff: false); }
        using var deadline = new CancellationTokenSource(remaining ?? TimeSpan.FromMilliseconds(_settings.ConnectTimeoutMs), _time);
        using var connect = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        try
        {
            _stream = await new ObservedTestConnector(_connector, trace).ConnectAsync(_settings.Host, _settings.Port, connect.Token);
            connect.Token.ThrowIfCancellationRequested();
            _client = new ModbusReadClient(_stream, _settings.Format, _time);
            _health?.ConnectionReplaced();
            _retryStarted = null;
            return Result("success", true);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { Reset(backoff: false); return Result("timeout", false); }
        catch (Exception exception) when (exception is IOException or SocketException)
        {
            Reset(backoff: false);
            var code = exception is OutboundAccessDeniedException ? "outbound.denied" : SocketError(exception) switch
            {
                System.Net.Sockets.SocketError.HostNotFound or System.Net.Sockets.SocketError.NoData or System.Net.Sockets.SocketError.TryAgain => "dns.failed",
                System.Net.Sockets.SocketError.ConnectionRefused => "connection.refused",
                System.Net.Sockets.SocketError.TimedOut => "timeout",
                _ => "connection.failed"
            };
            return Result(code, false);
        }
        catch
        { Reset(backoff: false); throw; }

        ModbusConnectionTestResult Result(string code, bool success, bool reused = false) =>
            new(success, code, _time.GetElapsedTime(started).TotalMilliseconds, trace.FinishModbus(code, success, reused));
    }

    internal Task<ModbusReadResult> ReadAsync(byte unit, byte function, ushort address, ushort count, CancellationToken token) =>
        ReadCoreAsync(unit, function, address, count, false, token);

    // 预览允许读取尚未启用的草稿 UnitId，但不得扩充运行时健康成员或记一次轮询失败。
    internal Task<ModbusReadResult> ReadPreviewAsync(byte unit, byte function, ushort address, ushort count, CancellationToken token) =>
        ReadCoreAsync(unit, function, address, count, true, token);

    private async Task<ModbusReadResult> ReadCoreAsync(byte unit, byte function, ushort address, ushort count, bool preview, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        token.ThrowIfCancellationRequested();
        if (!preview)
        {
            if (_units is null || _health is null)
            { throw new InvalidOperationException("读取前须绑定运行时从站集合。"); }
            if (!_units.Contains(unit))
            { throw new ArgumentOutOfRangeException(nameof(unit)); }
        }
        if (_settings.Format == ModbusFrameFormat.RtuOverTcp && unit is < 1 or > 247)
        { throw new ArgumentOutOfRangeException(nameof(unit)); }
        if (function is < 1 or > 4 || count == 0 || count > (function <= 2 ? 2000 : 125) || (int)address + count > 65536)
        { throw new ArgumentException("Modbus 读取块范围无效。"); }

        if (_retryStarted is { } failedAt && _time.GetElapsedTime(failedAt) < _retryDelay)
        { return new(ModbusRequestOutcome.ConnectionError, false); }

        // 排队/等待请求间隔时取消不能关闭健康连接；此时尚未注册读取的 abort。
        if (_lastFinished is { } finished)
        {
            var remaining = TimeSpan.FromMilliseconds(_settings.RequestIntervalMs) - _time.GetElapsedTime(finished);
            if (remaining > TimeSpan.Zero)
            { await Task.Delay(remaining, _time, token); }
        }
        if (_client?.NeedsNewConnection == true)
        { Reset(backoff: false); }
        if (_client is null)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(_settings.ConnectTimeoutMs), _time);
            using var connect = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
            try
            {
                _stream = await _connector.ConnectAsync(_settings.Host, _settings.Port, connect.Token);
                connect.Token.ThrowIfCancellationRequested();
                _client = new ModbusReadClient(_stream, _settings.Format, _time);
                _health?.ConnectionReplaced();
                _retryStarted = null;
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                if (!preview)
                { _observation.Failed("modbus.connectTimeout"); }
                Reset(backoff: true);
                return new(ModbusRequestOutcome.ConnectionError, false, ConnectionAttempted: true);
            }
            catch (Exception exception) when (exception is IOException or SocketException)
            {
                if (!preview)
                { _observation.Failed(exception is OutboundAccessDeniedException ? "outbound.denied" : "modbus.connectionFailed"); }
                Reset(backoff: true);
                return new(ModbusRequestOutcome.ConnectionError, false, ConnectionAttempted: true);
            }
            catch
            { Reset(backoff: false); throw; }
        }

        var client = _client;
        ModbusReadResult result;
        var aborted = 0;
        try
        {
            var timeout = TimeSpan.FromMilliseconds(_settings.ReadTimeoutMs);
            result = await _executor.ExecuteAsync(() => function <= 2
                ? new ModbusReadResult(ModbusRequestOutcome.Success, true, Bits: client.ReadBits(unit, function, address, count, timeout))
                : new ModbusReadResult(ModbusRequestOutcome.Success, true, Registers: client.ReadRegisters(unit, function, address, count, timeout)),
                () => { Interlocked.Exchange(ref aborted, 1); client.Abort(); }, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // 执行器尚未准入的取消没有关闭流，不把它升级成连接故障。
            if (Volatile.Read(ref aborted) != 0)
            { Reset(backoff: false); }
            throw;
        }
        catch (ModbusDeviceException exception)
        { result = new(ModbusRequestOutcome.DeviceException, true, ExceptionCode: exception.Code); }
        catch (Exception exception) when (exception is IOException or SocketException or TimeoutException or ObjectDisposedException)
        {
            var outcome = IsTimeout(exception) ? ModbusRequestOutcome.Timeout
                : client.NeedsNewConnection || exception is EndOfStreamException or ObjectDisposedException || SocketError(exception) is not null
                    ? ModbusRequestOutcome.ConnectionError : ModbusRequestOutcome.ReadError;
            result = new(outcome, true);
        }
        finally { _lastFinished = _time.GetTimestamp(); }

        if (result.Outcome == ModbusRequestOutcome.Success)
        { _reconnectSeconds = 1; }
        var reconnect = preview ? result.Outcome == ModbusRequestOutcome.ConnectionError : _health!.Record(unit, result.Outcome);
        if (!preview)
        {
            // 合法异常应答也证明从站能够响应；单个从站超时不污染共享连接。
            // 预览/纯建连、跳过、排队取消均不冒充正式采集的成功或失败。
            if (result.Outcome is ModbusRequestOutcome.Success or ModbusRequestOutcome.DeviceException)
            { _observation.Succeeded(); }
            else if (reconnect)
            { _observation.Failed(result.Outcome == ModbusRequestOutcome.Timeout ? "modbus.allUnitsTimedOut" : "modbus.connectionFailed"); }
            else if (result.Outcome == ModbusRequestOutcome.ReadError)
            { _observation.Failed("modbus.invalidResponse"); }
        }
        if (reconnect)
        { Reset(backoff: true); }
        else if (result.Outcome == ModbusRequestOutcome.Timeout && _settings.Format == ModbusFrameFormat.RtuOverTcp)
        {
            try
            {
                var drained = await ModbusRtuDrain.DrainAsync(_stream!, TimeSpan.FromMilliseconds(_settings.RtuSilenceMs), _time, token);
                if (!preview && drained.ConnectionClosed)
                { _observation.Failed("modbus.connectionFailed"); }
                if (!drained.CanReuseConnection)
                { Reset(backoff: false); }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            { Reset(backoff: false); throw; }
            catch (Exception exception) when (exception is IOException or SocketException)
            {
                if (!preview)
                { _observation.Failed("modbus.connectionFailed"); }
                Reset(backoff: true);
            }
        }
        return result;
    }

    private static System.Net.Sockets.SocketError? SocketError(Exception exception) =>
        exception is SocketException socket ? socket.SocketErrorCode
            : exception.InnerException is { } inner ? SocketError(inner) : null;

    private static bool IsTimeout(Exception exception) => exception is TimeoutException ||
        SocketError(exception) is System.Net.Sockets.SocketError.TimedOut or System.Net.Sockets.SocketError.WouldBlock;

    private void Reset(bool backoff)
    {
        _client?.Dispose();
        _client = null;
        _stream?.Dispose();
        _stream = null;
        if (backoff)
        {
            _retryStarted = _time.GetTimestamp();
            _retryDelay = TimeSpan.FromSeconds(_reconnectSeconds);
            _reconnectSeconds = Math.Min(60, _reconnectSeconds * 2);
        }
        else
        { _retryStarted = null; }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        { return; }
        _disposed = true;
        try
        { await _executor.DisposeAsync(); }
        finally { Reset(backoff: false); }
    }
}
