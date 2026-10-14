using System.Net;
using System.Net.Sockets;

namespace TenonEdge.Runtime.Connections;

public sealed record OutboundNetworkRule(IPNetwork Network, int Port);
public enum OutboundAddressDecision { Allowed, PublicGrantRequired, LocalPortRequired, Forbidden }

/// <summary>
/// 出站规则的不可变快照（功能 §2.5）。本机地址与管理端口由宿主在连接时提供，避免网卡变化后遗漏本机管理服务。
/// 公网 CIDR 白名单不覆盖危险地址拒绝；本机端口例外单独登记，仍不能放行管理端口。
/// </summary>
public sealed class OutboundAddressRules
{
    private static readonly IPNetwork[] PrivateNetworks =
    [IPNetwork.Parse("10.0.0.0/8"), IPNetwork.Parse("172.16.0.0/12"), IPNetwork.Parse("192.168.0.0/16"), IPNetwork.Parse("fc00::/7")];
    private readonly OutboundNetworkRule[] _networks;
    private readonly HashSet<int> _localPorts;

    public OutboundAddressRules(IEnumerable<OutboundNetworkRule> networks, IEnumerable<int> localPorts)
    {
        _networks = networks.ToArray();
        _localPorts = localPorts.ToHashSet();
        foreach (var port in _networks.Select(rule => rule.Port).Concat(_localPorts))
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
        }
    }

    public bool IsAllowed(IPAddress address, int port, IEnumerable<IPAddress> localAddresses, IReadOnlySet<int> managementPorts) =>
        Check(address, port, localAddresses, managementPorts) == OutboundAddressDecision.Allowed;

    /// <summary>管理页面与实际连接共用同一判定；只有 PublicGrantRequired 可建议主机 CIDR 授权。</summary>
    public OutboundAddressDecision Check(IPAddress address, int port, IEnumerable<IPAddress> localAddresses, IReadOnlySet<int> managementPorts)
    {
        if (port is < 1 or > 65535)
        { return OutboundAddressDecision.Forbidden; }
        address = Normalize(address);
        if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.Broadcast)
            || address.IsIPv6LinkLocal || address.IsIPv6Multicast)
        { return OutboundAddressDecision.Forbidden; }
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            if ((bytes[0] == 169 && bytes[1] == 254) || bytes[0] is >= 224 and <= 239)
            { return OutboundAddressDecision.Forbidden; }
        }
        var local = IPAddress.IsLoopback(address) || localAddresses.Any(item => Normalize(item).Equals(address));
        if (local)
        {
            return managementPorts.Contains(port) ? OutboundAddressDecision.Forbidden
                : _localPorts.Contains(port) ? OutboundAddressDecision.Allowed : OutboundAddressDecision.LocalPortRequired;
        }
        return PrivateNetworks.Any(network => network.Contains(address))
            || _networks.Any(rule => rule.Port == port && rule.Network.Contains(address))
            ? OutboundAddressDecision.Allowed : OutboundAddressDecision.PublicGrantRequired;
    }

    private static IPAddress Normalize(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}
