using System.Net;
using TenonEdge.Runtime.Connections;

namespace TenonEdge.UnitTests.Runtime;

public sealed class OutboundAddressRulesTests
{
    [Theory]
    [InlineData("203.0.113.5", 443, OutboundAddressDecision.PublicGrantRequired)]
    [InlineData("::ffff:203.0.113.5", 443, OutboundAddressDecision.PublicGrantRequired)]
    [InlineData("2001:db8::5", 443, OutboundAddressDecision.PublicGrantRequired)]
    [InlineData("10.1.2.3", 443, OutboundAddressDecision.Allowed)]
    [InlineData("127.0.0.1", 443, OutboundAddressDecision.LocalPortRequired)]
    [InlineData("203.0.113.10", 443, OutboundAddressDecision.LocalPortRequired)]
    [InlineData("203.0.113.10", 8080, OutboundAddressDecision.Forbidden)]
    [InlineData("::1", 8080, OutboundAddressDecision.Forbidden)]
    [InlineData("169.254.169.254", 443, OutboundAddressDecision.Forbidden)]
    [InlineData("224.0.0.1", 443, OutboundAddressDecision.Forbidden)]
    [InlineData("fe80::1", 443, OutboundAddressDecision.Forbidden)]
    public void GrantAdviceUsesTheSameVerdictAsTheConnection(string address, int port, OutboundAddressDecision decision)
    {
        var rules = new OutboundAddressRules([], []);
        IPAddress[] local = [IPAddress.Parse("203.0.113.10")];
        var management = new HashSet<int> { 8080 };
        var ip = IPAddress.Parse(address);
        Assert.Equal(decision, rules.Check(ip, port, local, management));
        Assert.Equal(decision == OutboundAddressDecision.Allowed, rules.IsAllowed(ip, port, local, management));
    }

    [Theory]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.254", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("192.168.5.5", true)]
    [InlineData("fc00::1", true)]
    [InlineData("fdff::1", true)]
    [InlineData("fe80::1", false)]
    [InlineData("8.8.8.8", false)]
    [InlineData("::ffff:10.1.2.3", true)]
    [InlineData("::ffff:127.0.0.1", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("::", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("ff02::1", false)]
    public void DefaultsAllowOnlyPrivateNonLocalDestinations(string address, bool allowed)
    {
        var rules = new OutboundAddressRules([], []);
        Assert.Equal(allowed, rules.IsAllowed(IPAddress.Parse(address), 1883, [], new HashSet<int>()));
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::1")]
    [InlineData("192.168.1.10")]
    [InlineData("::ffff:192.168.1.10")]
    public void LocalExceptionNeverOverridesManagementPorts(string address)
    {
        var rules = new OutboundAddressRules([new(IPNetwork.Parse("0.0.0.0/0"), 8080)], [1883, 8080]);
        IPAddress[] local = [IPAddress.Parse("192.168.1.10")];
        var management = new HashSet<int> { 8080 };
        Assert.True(rules.IsAllowed(IPAddress.Parse(address), 1883, local, management));
        Assert.False(rules.IsAllowed(IPAddress.Parse(address), 8080, local, management));
        Assert.False(rules.IsAllowed(IPAddress.Parse(address), 8883, local, management));
    }

    [Fact]
    public void ExplicitNetworksArePortScopedAndCannotAllowMetadataOrMulticast()
    {
        var rules = new OutboundAddressRules([new(IPNetwork.Parse("0.0.0.0/0"), 443), new(IPNetwork.Parse("::/0"), 443)], [443]);
        var management = new HashSet<int>();
        Assert.True(rules.IsAllowed(IPAddress.Parse("8.8.8.8"), 443, [], management));
        Assert.False(rules.IsAllowed(IPAddress.Parse("8.8.8.8"), 80, [], management));
        foreach (var denied in new[] { "169.254.169.254", "::ffff:169.254.169.254", "224.0.0.1", "255.255.255.255", "::", "fe80::1", "ff02::1" })
        {
            Assert.False(rules.IsAllowed(IPAddress.Parse(denied), 443, [], management));
        }
    }
}
