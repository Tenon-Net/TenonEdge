using System.Text.Json;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Plans;

namespace TenonEdge.Adapters.Mqtt;

public sealed class MqttOutputFactory(ICredentialResolver credentials, TimeProvider time, IOutboundConnector connector, ISharedConnections connections, string? gatewayNodeId = null, ITlsTrustResolver? trust = null) : IOutputAdapterFactory
{
    // Host 传持久 NodeId，使不同网关不会用相同默认 ClientId 相互踢下线。
    // 独立装配没有网关身份时使用工厂实例内固定的随机前缀；输出采用 CleanSession，不依赖跨进程会话。
    private readonly string _defaultClientPrefix = "tenon-" + (gatewayNodeId is null ? Guid.NewGuid() : Guid.Parse(gatewayNodeId)).ToString("N");

    public string Kind => "output.mqtt";
    public RuntimeUnitId? RecoveryGroup(RuntimeUnitPlan plan) => MqttPlanAdapters.RecoveryGroup(plan.Configuration.GetProperty("connectionId").GetInt64());

    // 仅计划范围装配使用；默认输出独立路径不访问或修改输入组。
    internal Func<MqttOutputSettings, MqttConnectionGroup?>? InputGroups { get; init; }


    public IOutputTarget Build(RuntimeUnitPlan plan)
    {
        try
        {
            var settings = MqttOutputSettings.Parse(plan.Configuration);
            if (settings.QueueCredentialReferenceMissing)
            { throw new RuntimeUnitBuildException(plan.Id, "旧快照未记录此 MQTT 连接的凭证引用，发送接续不可用；请核对并重新应用配置。"); }
            if (settings.UsePassword && credentials.ResolveMqtt(settings.ConnectionId, settings.Recipient) is null)
            {
                throw new ArgumentException("MQTT 认证凭证未设置或接收方已变化，须重新填写凭证。");
            }

            var inputGroup = InputGroups?.Invoke(settings);
            inputGroup?.ValidateMember(settings, settings.ClientId ?? $"{_defaultClientPrefix}-{settings.ConnectionId}");
            return new MqttOutputTarget(plan.Id, settings, credentials, time, _defaultClientPrefix, connector, connections, inputGroup, trust);
        }
        catch (Exception exception) when (exception is ArgumentException or JsonException)
        {
            // 解析器可回显未知属性名，凭证提供者异常也可能含秘密；不能把原异常链写入状态或日志。
            throw new RuntimeUnitBuildException(plan.Id, "MQTT 输出配置或凭证无效，请检查连接、主题和认证设置。");
        }
    }
}
