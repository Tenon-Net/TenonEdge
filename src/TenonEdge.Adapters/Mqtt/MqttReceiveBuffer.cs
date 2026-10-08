using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Channels;
using MQTTnet;
using MQTTnet.Adapter;
using MQTTnet.Exceptions;
using MQTTnet.Formatter;
using MQTTnet.Packets;
using TenonEdge.Abstractions.Contracts;

namespace TenonEdge.Adapters.Mqtt;

/// <summary>一个连接代次的原始接收所有者：先预约再解码，PUBLISH 不进入库的无界队列。
/// 控制包继续由原客户端处理；取消不抢先归还解码中或正在执行回调的额度。</summary>
internal sealed class MqttReceiveBuffer(IInputBufferBudget budget, int capacity,
    Func<MqttApplicationMessageReceivedEventArgs, CancellationToken, Task> receive, Action<string> report, TimeProvider? time = null) : IDisposable
{
    private readonly Lock _lock = new();
    private readonly Channel<Entry> _queue = Channel.CreateBounded<Entry>(new BoundedChannelOptions(capacity)
    { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly CancellationTokenSource _stop = new();
    private Reservation? _pending;
    private int _count;
    private bool _closed;
    private bool _wrapped;
    private TaskCompletionSource _space = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task _cancellation = Task.CompletedTask;
    private Task _consumer = Task.CompletedTask;
    private TaskCompletionSource? _readCompletion;
    internal Task Completion
    {
        get { lock (_lock) { return Task.WhenAll(_consumer, _readCompletion?.Task ?? Task.CompletedTask); } }
    }

    internal async ValueTask<bool> AdmitAsync(byte header, int length, CancellationToken cancellationToken)
    {
        if ((header >> 4) != 3)
        { return true; }
        var qos = (header >> 1) & 3;
        if (qos > 1)
        { throw new MqttProtocolViolationException("不支持 QoS 2 输入。"); }
        // 正文数组、最坏 UTF-16 Topic 及包/回调/队列/租约的固定开销。
        var bytes = checked(3L * length + 4096);
        if (qos == 0)
        {
            lock (_lock)
            {
                ValidateAdmission();
                if (_count < capacity && budget.TryReserveBuffer(bytes) is { } lease)
                { AdmitLease(lease); return true; }
            }
            report("bufferDropped");
            return false;
        }
        // QoS 1 不立即断连：给当前持久接管让出时间，避免积压重投每次都抢先中止同一批消息。
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5), time ?? TimeProvider.System);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            while (true)
            {
                Task changed;
                lock (_lock)
                {
                    ValidateAdmission();
                    if (_count < capacity)
                    { break; }
                    changed = _space.Task;
                }
                await changed.WaitAsync(operation.Token);
            }
            var lease = await budget.ReserveBufferAsync(bytes, operation.Token);
            lock (_lock)
            {
                try
                { ValidateAdmission(); AdmitLease(lease); }
                catch { lease.Dispose(); throw; }
            }
            return true;
        }
        catch (Exception exception) when (exception is ArgumentOutOfRangeException ||
            exception is OperationCanceledException && deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            report("bufferFull");
            throw new MqttCommunicationException("MQTT 接收预算不足，未确认消息等待重投。");
        }
    }

    private void ValidateAdmission()
    {
        if (_closed)
        { throw new OperationCanceledException(); }
        if (_pending is not null)
        { throw new InvalidOperationException("MQTT 解码预约未结束。"); }
    }

    private void AdmitLease(IDisposable lease)
    {
        _pending = new Reservation(this, lease);
        _count++;
    }

    internal IMqttChannelAdapter Wrap(IMqttChannelAdapter inner, string clientId)
    {
        lock (_lock)
        {
            if (_wrapped || _closed)
            { throw new InvalidOperationException("接收缓冲不能跨连接代次复用。"); }
            _wrapped = true;
        }
        _consumer = ConsumeAsync(inner, clientId);
        return new Adapter(this, inner);
    }

    private async Task<MqttPacket> ReceiveAsync(IMqttChannelAdapter inner, CancellationToken token)
    {
        TaskCompletionSource completed;
        lock (_lock)
        {
            if (_closed)
            { throw new OperationCanceledException(); }
            completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _readCompletion = completed;
        }
        try
        {
            while (true)
            {
                var packet = await inner.ReceivePacketAsync(token);
                if (packet is not MqttPublishPacket publish)
                { return packet; }
                lock (_lock)
                {
                    var reservation = _pending ?? throw new InvalidOperationException("MQTT PUBLISH 未经过分配前预约。");
                    _pending = null;
                    var entry = new Entry(publish, reservation);
                    if (_closed || !_queue.Writer.TryWrite(entry))
                    { entry.Dispose(); }
                }
                token.ThrowIfCancellationRequested();
            }
        }
        finally
        {
            lock (_lock)
            {
                _pending?.Dispose();
                _pending = null;
                completed.TrySetResult();
            }
        }
    }

    private async Task ConsumeAsync(IMqttChannelAdapter inner, string clientId)
    {
        try
        {
            await foreach (var entry in _queue.Reader.ReadAllAsync(_stop.Token))
            {
                using (entry)
                {
                    var message = MqttApplicationMessageFactory.Create(entry.Packet);
                    var args = new MqttApplicationMessageReceivedEventArgs(clientId, message, entry.Packet,
                        (ack, token) => ack.ProcessingFailed ? Task.CompletedTask : inner.SendPacketAsync(MqttPubAckPacketFactory.Create(ack), token));
                    try
                    { await receive(args, _stop.Token); }
                    finally { message.Payload = default; message.Topic = string.Empty; }
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException) { report("bufferFailed"); }
        finally
        {
            Close();
            while (_queue.Reader.TryRead(out var entry))
            { entry.Dispose(); }
            inner.Dispose();
            await _cancellation;
            _stop.Dispose();
        }
    }

    private void Close()
    {
        lock (_lock)
        {
            if (_closed)
            { return; }
            _closed = true;
            _space.TrySetResult();
            _queue.Writer.TryComplete();
            _cancellation = _stop.CancelAsync();
            if (!_wrapped)
            { _stop.Dispose(); }
        }
        // 不等待回调，也不归还 _pending；各自的 finally 持有释放责任。
    }

    public void Dispose() => Close();

    private sealed class Reservation(MqttReceiveBuffer owner, IDisposable lease) : IDisposable
    {
        private IDisposable? _lease = lease;
        public void Dispose()
        {
            var current = Interlocked.Exchange(ref _lease, null);
            if (current is null)
            { return; }
            lock (owner._lock)
            {
                owner._count--;
                owner._space.TrySetResult();
                owner._space = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            current.Dispose();
        }
    }

    private sealed class Entry(MqttPublishPacket packet, Reservation reservation) : IDisposable
    {
        public MqttPublishPacket Packet { get; } = packet;
        public void Dispose()
        {
            Packet.Payload = default;
            Packet.Topic = string.Empty;
            reservation.Dispose();
        }
    }

    private sealed class Adapter(MqttReceiveBuffer owner, IMqttChannelAdapter inner) : IMqttChannelAdapter
    {
        public long BytesReceived => inner.BytesReceived;
        public long BytesSent => inner.BytesSent;
        public X509Certificate2 ClientCertificate => inner.ClientCertificate;
        public EndPoint RemoteEndPoint => inner.RemoteEndPoint;
        public EndPoint LocalEndPoint => inner.LocalEndPoint;
        public bool IsSecureConnection => inner.IsSecureConnection;
        public MqttPacketFormatterAdapter PacketFormatterAdapter => inner.PacketFormatterAdapter;
        public Task ConnectAsync(CancellationToken cancellationToken) => inner.ConnectAsync(cancellationToken);
        public Task DisconnectAsync(CancellationToken cancellationToken) { owner.Close(); return inner.DisconnectAsync(cancellationToken); }
        public Task<MqttPacket> ReceivePacketAsync(CancellationToken cancellationToken) => owner.ReceiveAsync(inner, cancellationToken);
        public void ResetStatistics() => inner.ResetStatistics();
        public Task SendPacketAsync(MqttPacket packet, CancellationToken cancellationToken) => inner.SendPacketAsync(packet, cancellationToken);
        public void Dispose() { owner.Close(); inner.Dispose(); }
    }
}
