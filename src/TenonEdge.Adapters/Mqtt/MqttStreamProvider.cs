using System.Net;
using MQTTnet.Channel;
using TenonEdge.Abstractions.Contracts;

namespace TenonEdge.Adapters.Mqtt;

/// <summary>MQTTnet 5.2 StreamProvider：只提供已校验的 TCP 流，TLS/SNI 仍由库按原主机名执行。</summary>
internal sealed class MqttStreamProvider(IOutboundConnector connector) : IMqttClientStreamProvider
{
    public Task<Stream> ConnectAsync(EndPoint brokerEndPoint, CancellationToken cancellationToken) => brokerEndPoint switch
    {
        DnsEndPoint dns => connector.ConnectAsync(dns.Host, dns.Port, cancellationToken).AsTask(),
        IPEndPoint ip => connector.ConnectAsync(ip.Address.ToString(), ip.Port, cancellationToken).AsTask(),
        _ => throw new ArgumentException("不支持的 MQTT 目标地址。", nameof(brokerEndPoint)),
    };
}
