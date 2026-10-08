using TenonEdge.Abstractions.Contracts;

namespace TenonEdge.Adapters.Mqtt;

/// <summary>每个准备计划创建一套 MQTT 输入/输出绑定；服务依赖保持进程生命周期，共享连接管理不另建实例。</summary>
public sealed class MqttPlanAdapterProvider(ICredentialResolver credentials, IJsonInputDecoderFactory decoders,
    ISharedConnections connections, IOutboundConnector connector, TimeProvider time, string gatewayNodeId,
    Action<long, string> report) : IRuntimePlanAdapterProvider
{
    public RuntimePlanAdapters Create()
    {
        var adapters = new MqttPlanAdapters(credentials, decoders, connections, connector, time, gatewayNodeId, report);
        return new([adapters.InputFactory], [adapters.OutputFactory]);
    }
}
