using System.Net;
using TenonEdge.Runtime.Connections;

namespace TenonEdge.Host.Features.Connections;

/// <summary>运行时和只读管理视图共用的部署规则解析；网页只修改持久规则，不能撤销部署授权。</summary>
internal sealed record OutboundDeploymentRules(OutboundNetworkGrant[] Networks, int[] LocalPorts)
{
    public static OutboundDeploymentRules Read(IConfiguration configuration)
    {
        var networks = configuration.GetSection("TenonEdge:Outbound:Networks").GetChildren().Select(rule =>
            new OutboundNetworkGrant(IPNetwork.Parse(rule["Cidr"] ?? throw new InvalidOperationException("出站规则缺少 CIDR。")).ToString(),
                rule.GetValue<int>("Port"))).ToArray();
        var localPorts = configuration.GetSection("TenonEdge:Outbound:LocalPorts").Get<int[]>() ?? [];
        _ = new OutboundAddressRules(networks.Select(rule => new OutboundNetworkRule(IPNetwork.Parse(rule.Cidr), rule.Port)), localPorts);
        return new(networks, localPorts);
    }
}
