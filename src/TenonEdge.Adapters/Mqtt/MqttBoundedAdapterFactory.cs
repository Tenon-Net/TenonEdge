using MQTTnet;
using MQTTnet.Adapter;
using MQTTnet.Channel;
using MQTTnet.Diagnostics.Logger;
using MQTTnet.Formatter;
using MQTTnet.Implementations;
using TenonEdge.Adapters.Diagnostics;

namespace TenonEdge.Adapters.Mqtt;

/// <summary>使用库的 TCP、编码器和通道适配器；自有 TLS 通道禁止证书 URL 外联，帧包装负责分配前长度限制。</summary>
internal sealed class MqttBoundedAdapterFactory(Action oversized, Func<byte, int, bool>? admit = null, MqttReceiveBuffer? receiveBuffer = null,
    Action? connected = null, TlsClientCertificate? clientCertificate = null) : IMqttClientAdapterFactory
{
    // 3.1.1 的 PUBLISH：允许完整 1 MiB 载荷、最长 UTF-8 Topic、Topic 长度字段及 QoS 1 PacketId。
    // 限制 Remaining Length 而不是只限载荷；业务回调仍独立执行 1 MiB 载荷上限。
    internal const int MaximumRemainingLength = MqttInputReceiver.MaximumPayloadBytes + ushort.MaxValue + 4;

    public IMqttChannelAdapter CreateClientAdapter(MqttClientOptions options, MqttPacketInspector packetInspector, IMqttNetLogger logger)
    {
        if (options.ProtocolVersion != MqttProtocolVersion.V311 || options.ChannelOptions is not MqttClientTcpOptions tcp)
        { throw new NotSupportedException("接收长度保护仅用于 MQTT 3.1.1 TCP/TLS。"); }
        if (clientCertificate is not null && tcp.TlsOptions?.UseTls != true)
        { throw new ArgumentException("客户端证书认证要求 MQTT TLS，不能降级为明文连接。"); }
        if (clientCertificate is not null && options.Credentials is not null)
        { throw new ArgumentException("MQTT 客户端证书不能与账号密码同时使用。"); }
        // 所有 TLS 使用同一策略，由实际选项选择；调用方不能遗漏另一个离线开关。
        IMqttChannel transport = tcp.TlsOptions?.UseTls == true
            ? new MqttTlsChannel(tcp, clientCertificate) : new MqttTcpChannel(options);
        var channel = new MqttFrameLimitChannel(transport, MaximumRemainingLength, oversized, admit, receiveBuffer is null ? null : receiveBuffer.AdmitAsync, connected);
        var formatter = new MqttPacketFormatterAdapter(options.ProtocolVersion,
            new MqttBufferWriter(options.WriterBufferSize, options.WriterBufferSizeMax));
        var adapter = new MqttChannelAdapter(channel, formatter, logger)
        {
            PacketInspector = packetInspector,
            AllowPacketFragmentation = options.AllowPacketFragmentation,
        };
        return receiveBuffer?.Wrap(adapter, options.ClientId) ?? adapter;
    }
}
