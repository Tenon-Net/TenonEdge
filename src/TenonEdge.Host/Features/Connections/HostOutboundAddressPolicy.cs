using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Data.Sqlite;
using SqlSugar;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Host.Bootstrap;
using TenonEdge.Runtime.Connections;

namespace TenonEdge.Host.Features.Connections;

/// <summary>部署规则与审计化持久规则合并；每次新连接重新检查，规则读取失败时不放行。</summary>
internal sealed class HostOutboundAddressPolicy(IConfiguration configuration, ListenerSettings listeners, IServer server, Func<OutboundPolicyState> readStored) : IOutboundAddressPolicy
{
    public bool IsAllowed(IPAddress address, int port)
    {
        var management = new HashSet<int>();
        if (listeners.Http is { } http)
        { management.Add(http.Port); }
        management.Add(listeners.Https.Port);
        if (management.Remove(0))
        {
            var bound = server.Features.Get<IServerAddressesFeature>()?.Addresses;
            if (bound is null || bound.Count == 0)
            {
                // 动态端口尚未绑定，不能拿 0 当作管理端口后放过本机访问。由连接重试等待宿主就绪。
                throw new IOException("管理监听地址尚未就绪，暂不建立出站连接。");
            }
            foreach (var endpoint in bound)
            { management.Add(new Uri(endpoint).Port); }
        }

        OutboundAddressRules rules;
        try
        {
            var networks = configuration.GetSection("TenonEdge:Outbound:Networks").GetChildren().Select(rule =>
                new OutboundNetworkRule(IPNetwork.Parse(rule["Cidr"] ?? throw new InvalidOperationException("出站规则缺少 CIDR。")),
                    rule.GetValue<int>("Port"))).ToArray();
            var localPorts = configuration.GetSection("TenonEdge:Outbound:LocalPorts").Get<int[]>() ?? [];
            var stored = readStored();
            rules = new OutboundAddressRules(networks.Concat(stored.Networks.Select(rule => new OutboundNetworkRule(IPNetwork.Parse(rule.Cidr), rule.Port))),
                localPorts.Concat(stored.LocalPorts));
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or InvalidOperationException)
        {
            // 非法部署规则不能放行，也不能把配置错误计入投递的毒消息次数。
            throw new OutboundAccessDeniedException();
        }
        catch (Exception exception) when (exception is SqliteException or SqlSugarException or JsonException)
        {
            throw new IOException("无法读取出站规则，暂不建立出站连接。", exception);
        }
        IPAddress[] local;
        try
        {
            local = NetworkInterface.GetAllNetworkInterfaces().SelectMany(network => network.GetIPProperties().UnicastAddresses)
                .Select(info => info.Address).ToArray();
        }
        catch (NetworkInformationException exception)
        {
            throw new IOException("无法确认本机地址，暂不建立出站连接。", exception);
        }
        return rules.IsAllowed(address, port, local, management);
    }
}
