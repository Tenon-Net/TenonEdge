using System.Globalization;
using System.Net;

namespace TenonEdge.Host.Bootstrap;

/// <summary>
/// 管理端口的监听设置（总设计 §10.3）：HTTP（默认 8080）与 HTTPS（默认 8443）同时开放，默认用 HTTP 访问；
/// 两者的地址和端口分别配置，部署参数可以关闭 HTTP 监听；不发送 HSTS。
/// 部署参数（环境变量写作 <c>TenonEdge__Http__Port</c> 等）：
/// <list type="bullet">
/// <item><c>TenonEdge:Http:Enabled</c>：是否开放 HTTP，默认 true。</item>
/// <item><c>TenonEdge:Http:Address</c>、<c>TenonEdge:Https:Address</c>：监听地址，默认 <c>*</c>（全部 IPv4 与 IPv6 地址）；
/// 也可以写 <c>localhost</c> 或具体的 IP 地址，例如只监听管理网卡的地址。</item>
/// <item><c>TenonEdge:Http:Port</c>、<c>TenonEdge:Https:Port</c>：端口；0 表示由系统分配，只用于测试。</item>
/// </list>
/// 显式配置监听后，ASP.NET Core 的 urls 参数不再生效。系统设置中的监听配置随 T3-09 实现。
/// </summary>
internal sealed record ListenerSettings(ListenEndpoint? Http, ListenEndpoint Https)
{
    public const int DefaultHttpPort = 8080;

    public const int DefaultHttpsPort = 8443;

    private const string Section = "TenonEdge";

    public static ListenerSettings Resolve(IConfiguration configuration)
    {
        var httpEnabled = ParseBoolean(configuration, $"{Section}:Http:Enabled", defaultValue: true);
        var https = ParseEndpoint(configuration, "Https", DefaultHttpsPort);
        var http = httpEnabled ? ParseEndpoint(configuration, "Http", DefaultHttpPort) : null;
        if (http is not null && http.Port != 0 && http.Port == https.Port)
        {
            throw new HostStartupException(HostExitCodes.ConfigurationError, $"HTTP 与 HTTPS 不能使用同一端口 {http.Port}。");
        }

        return new ListenerSettings(http, https);
    }

    private static ListenEndpoint ParseEndpoint(IConfiguration configuration, string name, int defaultPort)
    {
        var addressKey = $"{Section}:{name}:Address";
        var portKey = $"{Section}:{name}:Port";

        var addressValue = configuration[addressKey]?.Trim();
        ListenAddress address;
        if (string.IsNullOrEmpty(addressValue) || addressValue == "*")
        {
            address = ListenAddress.Any;
        }
        else if (string.Equals(addressValue, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            address = ListenAddress.Localhost;
        }
        else if (IPAddress.TryParse(addressValue, out var ip))
        {
            address = ListenAddress.For(ip);
        }
        else
        {
            throw new HostStartupException(
                HostExitCodes.ConfigurationError,
                $"部署参数 {addressKey} 的值“{addressValue}”不是有效的监听地址：只能写 *、localhost 或 IP 地址。");
        }

        var portValue = configuration[portKey];
        var port = defaultPort;
        if (!string.IsNullOrWhiteSpace(portValue)
            && (!int.TryParse(portValue, NumberStyles.None, CultureInfo.InvariantCulture, out port) || port > IPEndPoint.MaxPort))
        {
            throw new HostStartupException(
                HostExitCodes.ConfigurationError,
                $"部署参数 {portKey} 的值“{portValue}”不是有效的端口（0—{IPEndPoint.MaxPort}）。");
        }

        return new ListenEndpoint(address, port);
    }

    private static bool ParseBoolean(IConfiguration configuration, string key, bool defaultValue)
    {
        var value = configuration[key];
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        return bool.TryParse(value, out var parsed)
            ? parsed
            : throw new HostStartupException(HostExitCodes.ConfigurationError, $"部署参数 {key} 的值“{value}”不是 true 或 false。");
    }
}

/// <summary>一个监听端点。</summary>
internal sealed record ListenEndpoint(ListenAddress Address, int Port);

/// <summary>监听地址：全部地址、本机回环或指定的 IP 地址。</summary>
internal sealed record ListenAddress(ListenAddressKind Kind, IPAddress? Ip)
{
    public static ListenAddress Any { get; } = new(ListenAddressKind.Any, null);

    public static ListenAddress Localhost { get; } = new(ListenAddressKind.Localhost, null);

    public static ListenAddress For(IPAddress ip) => new(ListenAddressKind.Ip, ip);
}

internal enum ListenAddressKind
{
    Any,
    Localhost,
    Ip,
}
