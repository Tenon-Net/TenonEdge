using MQTTnet;
using MQTTnet.Adapter;
using MQTTnet.Diagnostics.Logger;
using MQTTnet.Formatter;
using MQTTnet.Implementations;

namespace TenonEdge.Adapters.Mqtt;

/// <summary>使用库的 TCP/TLS、编码器和通道适配器；自有包装只负责分配前的帧长度限制。</summary>
internal sealed class MqttBoundedAdapterFactory(Action oversized) : IMqttClientAdapterFactory
{
    // 3.1.1 的 PUBLISH：允许完整 1 MiB 载荷、最长 UTF-8 Topic、Topic 长度字段及 QoS 1 PacketId。
    // 限制 Remaining Length 而不是只限载荷；业务回调仍独立执行 1 MiB 载荷上限。
    internal const int MaximumRemainingLength = MqttInputReceiver.MaximumPayloadBytes + ushort.MaxValue + 4;

    public IMqttChannelAdapter CreateClientAdapter(MqttClientOptions options, MqttPacketInspector packetInspector, IMqttNetLogger logger)
    {
        if (options.ProtocolVersion != MqttProtocolVersion.V311 || options.ChannelOptions is not MqttClientTcpOptions)
        { throw new NotSupportedException("接收长度保护仅用于 MQTT 3.1.1 TCP/TLS。"); }
        var channel = new MqttFrameLimitChannel(new MqttTcpChannel(options), MaximumRemainingLength, oversized);
        var formatter = new MqttPacketFormatterAdapter(options.ProtocolVersion,
            new MqttBufferWriter(options.WriterBufferSize, options.WriterBufferSizeMax));
        return new MqttChannelAdapter(channel, formatter, logger)
        {
            PacketInspector = packetInspector,
            AllowPacketFragmentation = options.AllowPacketFragmentation,
        };
    }
}
