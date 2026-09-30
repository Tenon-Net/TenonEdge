using System.Net;
using Microsoft.Extensions.Configuration;
using TenonEdge.Host.Bootstrap;

namespace TenonEdge.UnitTests.Bootstrap;

/// <summary>
/// 管理端口的监听设置（总设计 §10.3）：默认 HTTP 8080 与 HTTPS 8443 监听全部地址；可分别配置地址与端口，可关闭 HTTP；
/// 参数错误以部署参数错误结束启动，而不是回退到默认值。
/// </summary>
public sealed class ListenerSettingsTests
{
    [Fact]
    public void DefaultsOpenHttpAndHttpsOnAllAddresses()
    {
        var settings = Resolve();

        Assert.Equal(new ListenEndpoint(ListenAddress.Any, 8080), settings.Http);
        Assert.Equal(new ListenEndpoint(ListenAddress.Any, 8443), settings.Https);
    }

    [Fact]
    public void HttpCanBeDisabledWhileHttpsStaysOpen()
    {
        var settings = Resolve(("TenonEdge:Http:Enabled", "false"));

        Assert.Null(settings.Http);
        Assert.Equal(8443, settings.Https.Port);
    }

    [Theory]
    [InlineData("*", nameof(ListenAddressKind.Any), null)]
    [InlineData("localhost", nameof(ListenAddressKind.Localhost), null)]
    [InlineData("127.0.0.1", nameof(ListenAddressKind.Ip), "127.0.0.1")]
    [InlineData("0.0.0.0", nameof(ListenAddressKind.Ip), "0.0.0.0")]
    [InlineData("::1", nameof(ListenAddressKind.Ip), "::1")]
    public void AddressesAndPortsAreConfiguredSeparately(string address, string kind, string? ip)
    {
        var settings = Resolve(("TenonEdge:Https:Address", address), ("TenonEdge:Https:Port", "9443"), ("TenonEdge:Http:Port", "9080"));

        Assert.Equal(Enum.Parse<ListenAddressKind>(kind), settings.Https.Address.Kind);
        Assert.Equal(ip is null ? null : IPAddress.Parse(ip), settings.Https.Address.Ip);
        Assert.Equal(9443, settings.Https.Port);
        Assert.Equal(new ListenEndpoint(ListenAddress.Any, 9080), settings.Http);
    }

    [Theory]
    [InlineData("TenonEdge:Https:Address", "gateway.example.com", "不是有效的监听地址")]
    [InlineData("TenonEdge:Https:Port", "65536", "不是有效的端口")]
    [InlineData("TenonEdge:Http:Port", "-1", "不是有效的端口")]
    [InlineData("TenonEdge:Http:Port", "http", "不是有效的端口")]
    [InlineData("TenonEdge:Http:Enabled", "yes", "不是 true 或 false")]
    public void InvalidValuesAreConfigurationErrors(string key, string value, string reason)
    {
        var exception = Assert.Throws<HostStartupException>(() => Resolve((key, value)));

        Assert.Equal(HostExitCodes.ConfigurationError, exception.ExitCode);
        Assert.Contains(key, exception.Message, StringComparison.Ordinal);
        Assert.Contains(reason, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void HttpAndHttpsCannotShareAPort()
    {
        var exception = Assert.Throws<HostStartupException>(() =>
            Resolve(("TenonEdge:Http:Port", "8443")));

        Assert.Contains("同一端口 8443", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SystemAssignedPortsMayBeUsedForBothListeners()
    {
        var settings = Resolve(("TenonEdge:Http:Port", "0"), ("TenonEdge:Https:Port", "0"));

        Assert.Equal(0, settings.Http!.Port);
        Assert.Equal(0, settings.Https.Port);
    }

    private static ListenerSettings Resolve(params (string Key, string Value)[] values) =>
        ListenerSettings.Resolve(new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(value => new KeyValuePair<string, string?>(value.Key, value.Value)))
            .Build());
}
