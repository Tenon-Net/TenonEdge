using System.Text.Json;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Plans;

namespace TenonEdge.Adapters.Mqtt;

public sealed class MqttOutputFactory(ICredentialResolver credentials, TimeProvider time, string? gatewayNodeId = null) : IOutputAdapterFactory
{
    // Host 传持久 NodeId，使不同网关不会用相同默认 ClientId 相互踢下线。
    // 独立装配没有网关身份时使用工厂实例内固定的随机前缀；输出采用 CleanSession，不依赖跨进程会话。
    private readonly string _defaultClientPrefix = "tenon-" + (gatewayNodeId is null ? Guid.NewGuid() : Guid.Parse(gatewayNodeId)).ToString("N");

    public string Kind => "output.mqtt";

    public IOutputTarget Build(RuntimeUnitPlan plan)
    {
        try
        {
            var settings = MqttOutputSettings.Parse(plan.Configuration);
            if (settings.UsePassword && credentials.ResolveMqtt(settings.ConnectionId, settings.Recipient) is null)
            {
                throw new ArgumentException("MQTT 认证凭证未设置或接收方已变化，须重新填写凭证。");
            }

            return new MqttOutputTarget(plan.Id, settings, credentials, time, _defaultClientPrefix);
        }
        catch (Exception exception) when (exception is ArgumentException or JsonException)
        {
            throw new RuntimeUnitBuildException(plan.Id, exception.Message, exception);
        }
    }
}
