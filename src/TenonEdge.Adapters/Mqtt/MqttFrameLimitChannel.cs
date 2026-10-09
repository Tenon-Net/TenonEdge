using System.Buffers;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using MQTTnet.Channel;
using MQTTnet.Exceptions;

namespace TenonEdge.Adapters.Mqtt;

/// <summary>
/// TCP/TLS 明文通道与 MQTTnet 解码器之间的固定头检查。完整头通过限长/准入检查后才交给解码器，
/// 防止先分配正文再检查。准入拒绝的帧以固定缓冲排空，不阻塞后面的控制报文。
/// 与 MQTTnet 接收循环一样只允许一个读取者，写入路径不变。
/// </summary>
internal sealed class MqttFrameLimitChannel(IMqttChannel inner, int maximumRemainingLength, Action oversized,
    Func<byte, int, bool>? admit = null, Func<byte, int, CancellationToken, ValueTask<bool>>? admitAsync = null, Action? connected = null) : IMqttChannel
{
    private readonly byte[] _header = new byte[5];
    private byte[]? _discard;
    private int _headerLength;
    private int _headerOffset;
    private int _remaining;
    private bool _faulted;

    public X509Certificate2 ClientCertificate => inner.ClientCertificate;
    public EndPoint RemoteEndPoint => inner.RemoteEndPoint;
    public EndPoint LocalEndPoint => inner.LocalEndPoint;
    public bool IsSecureConnection => inner.IsSecureConnection;
    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        await inner.ConnectAsync(cancellationToken);
        connected?.Invoke();
    }
    public Task DisconnectAsync(CancellationToken cancellationToken) => inner.DisconnectAsync(cancellationToken);
    public Task WriteAsync(ReadOnlySequence<byte> buffer, bool isEndOfPacket, CancellationToken cancellationToken) =>
        inner.WriteAsync(buffer, isEndOfPacket, cancellationToken);
    public void Dispose() => inner.Dispose();

    public async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_faulted)
        { throw new MqttProtocolViolationException("MQTT 帧读取已终止。"); }
        if (count == 0)
        { return 0; }
        try
        {
            if (_headerOffset < _headerLength)
            { return CopyHeader(buffer, offset, count); }
            if (_remaining > 0)
            {
                var read = await inner.ReadAsync(buffer, offset, Math.Min(count, _remaining), cancellationToken);
                _remaining -= read;
                return read;
            }
            while (true)
            {
                _headerLength = 0;
                _headerOffset = 0;
                // 缓存最多五字节固定头：拒绝时不能已有半个头暴露给库。
                if (await inner.ReadAsync(_header, 0, 1, cancellationToken) == 0)
                { return 0; }
                _headerLength = 1;
                for (var index = 0; index < 4; index++)
                {
                    if (await inner.ReadAsync(_header, _headerLength, 1, cancellationToken) == 0)
                    { throw new MqttProtocolViolationException("MQTT 固定头被截断。"); }
                    var current = _header[_headerLength++];
                    _remaining |= (current & 0x7f) << (7 * index);
                    if (index == 3 && (current & 0x80) != 0)
                    { throw new MqttProtocolViolationException("MQTT Remaining Length 编码无效。"); }
                    if (_remaining > maximumRemainingLength)
                    {
                        oversized();
                        throw new MqttProtocolViolationException("MQTT 帧声明的长度超过接收上限。");
                    }
                    if ((current & 0x80) == 0)
                    { break; }
                }
                var accepted = admitAsync is not null
                    ? await admitAsync(_header[0], _remaining, cancellationToken)
                    : admit?.Invoke(_header[0], _remaining) != false;
                if (accepted)
                { return CopyHeader(buffer, offset, count); }
                // 只允许丢弃合法 QoS 0 PUBLISH；QoS 1 必须由准入策略抛错断连，不能静默丢失。
                if ((_header[0] & 0xfe) != 0x30)
                { throw new MqttProtocolViolationException("只有 QoS 0 PUBLISH 可以在准入时丢弃。"); }
                _discard ??= new byte[4096];
                while (_remaining > 0)
                {
                    var read = await inner.ReadAsync(_discard, 0, Math.Min(_discard.Length, _remaining), cancellationToken);
                    if (read == 0)
                    { throw new MqttProtocolViolationException("MQTT 丢弃帧正文被截断。"); }
                    _remaining -= read;
                }
            }
        }
        catch
        {
            // 取消可能发生在已消费半个固定头/正文时；不能在同一代次重新解释残留字节。
            _faulted = true;
            throw;
        }
    }

    private int CopyHeader(byte[] buffer, int offset, int count)
    {
        var read = Math.Min(count, _headerLength - _headerOffset);
        _header.AsSpan(_headerOffset, read).CopyTo(buffer.AsSpan(offset, read));
        _headerOffset += read;
        return read;
    }
}
