using System.Net;
using TenonEdge.Host.Bootstrap;

namespace TenonEdge.Host.Security;

/// <summary>
/// 受信任的反向代理（总设计 §10.3）：部署参数 <c>TenonEdge:TrustedProxies</c> 列出代理的 IP 地址或网段（CIDR），
/// 例如环境变量 <c>TenonEdge__TrustedProxies__0=10.0.0.5</c>。首次设置之前只能用部署参数登记；
/// 系统设置中的登记与部署规则取并集。没有登记代理时不启用转发头中间件，忽略全部转发头。
/// </summary>
internal sealed class TrustedProxies
{
    public const string ConfigurationKey = "TenonEdge:TrustedProxies";

    private TrustedProxies(IReadOnlyList<System.Net.IPNetwork> networks) => Networks = networks;

    public IReadOnlyList<System.Net.IPNetwork> Networks { get; }

    public bool IsEmpty => Networks.Count == 0;

    public static TrustedProxies Resolve(IConfiguration configuration)
    {
        var networks = new List<System.Net.IPNetwork>();
        foreach (var entry in configuration.GetSection(ConfigurationKey).GetChildren())
        {
            var value = entry.Value?.Trim();
            if (string.IsNullOrEmpty(value))
            {
                continue;
            }

            if (TryParseNetwork(value, out var network))
            { networks.Add(network); }
            else
            {
                throw new HostStartupException(
                    HostExitCodes.ConfigurationError,
                    $"部署参数 {ConfigurationKey}:{entry.Key} 的值“{value}”不是 IP 地址或 CIDR 网段。");
            }
        }

        return new TrustedProxies(networks.AsReadOnly());
    }

    internal static TrustedProxies Parse(IEnumerable<string> values)
    {
        var networks = new List<System.Net.IPNetwork>();
        foreach (var raw in values)
        {
            var value = raw.Trim();
            if (TryParseNetwork(value, out var network))
            { networks.Add(network); }
            else
            { throw new ArgumentException("代理信任只接受IP地址或CIDR，不接受主机名、端口或转发头。"); }
        }
        return new TrustedProxies(networks.AsReadOnly());
    }

    private static bool TryParseNetwork(string value, out System.Net.IPNetwork network)
    {
        if (!IPAddress.TryParse(value, out var address))
        { return System.Net.IPNetwork.TryParse(value, out network); }
        network = new System.Net.IPNetwork(address, address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128);
        return true;
    }

    public bool Contains(IPAddress? address)
    {
        if (address is null)
        {
            return false;
        }

        var normalized = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        return Networks.Any(network => network.Contains(normalized));
    }
}
