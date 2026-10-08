using System.Buffers;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using MQTTnet.Channel;
using MQTTnet.Exceptions;

namespace TenonEdge.Adapters.Mqtt;

/// <summary>
/// 位于 MQTTnet 帧解码器与 TCP/TLS 明文通道之间：只检查固定头的 Remaining Length，
/// 在解码器取得完整头、按长度分配正文之前拒绝超限帧。不解释正文，不代替消息载荷校验或队列预算。
/// 与 MQTTnet 接收循环一样只允许一个读取者，写入路径不变。
/// </summary>
internal sealed class MqttFrameLimitChannel(IMqttChannel inner, int maximumRemainingLength, Action oversized) : IMqttChannel
{
    private enum Phase { Header, Length, Body }
    private Phase _phase;
    private int _lengthBytes;
    private int _remaining;
    private bool _faulted;

    public X509Certificate2 ClientCertificate => inner.ClientCertificate;
    public EndPoint RemoteEndPoint => inner.RemoteEndPoint;
    public EndPoint LocalEndPoint => inner.LocalEndPoint;
    public bool IsSecureConnection => inner.IsSecureConnection;
    public Task ConnectAsync(CancellationToken cancellationToken) => inner.ConnectAsync(cancellationToken);
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
        // 不把正文和下一帧头混在一次返回中。初始两字节可合并读取，其余长度字节逐个检查。
        var limit = _phase switch { Phase.Header => 2, Phase.Length => 1, _ => _remaining };
        var read = await inner.ReadAsync(buffer, offset, Math.Min(count, limit), cancellationToken);
        if (_phase == Phase.Body)
        {
            _remaining -= read;
            if (_remaining == 0)
            { _phase = Phase.Header; }
            return read;
        }
        for (var index = offset; index < offset + read; index++)
        {
            if (_phase == Phase.Header)
            {
                _phase = Phase.Length;
                _remaining = 0;
                _lengthBytes = 0;
                continue;
            }
            var current = buffer[index];
            _remaining |= (current & 0x7f) << (7 * _lengthBytes++);
            if (_lengthBytes == 4 && (current & 0x80) != 0)
            {
                _faulted = true;
                throw new MqttProtocolViolationException("MQTT Remaining Length 编码无效。");
            }
            if (_remaining > maximumRemainingLength)
            {
                _faulted = true;
                oversized();
                throw new MqttProtocolViolationException("MQTT 帧声明的长度超过接收上限。");
            }
            if ((current & 0x80) == 0)
            { _phase = _remaining == 0 ? Phase.Header : Phase.Body; }
        }
        return read;
    }
}
