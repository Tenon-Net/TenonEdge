using System.Net;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Configuration;
using TenonEdge.Host.Bootstrap;
using TenonEdge.Host.Features.Connections;

namespace TenonEdge.UnitTests.Runtime;

public sealed class HostOutboundPolicyTests
{
    [Fact]
    public void LocalExceptionComesFromCurrentConfigurationButCannotAllowManagementPort()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();
        using var server = new ServerStub();
        var policy = new HostOutboundAddressPolicy(configuration, ListenerSettings.Resolve(configuration), server, () => new(null, [], []));
        Assert.False(policy.IsAllowed(IPAddress.Loopback, 1883));
        configuration["TenonEdge:Outbound:LocalPorts:0"] = "1883";
        Assert.True(policy.IsAllowed(IPAddress.Loopback, 1883));
        configuration["TenonEdge:Outbound:LocalPorts:0"] = "8080";
        Assert.False(policy.IsAllowed(IPAddress.Loopback, 1883));
        Assert.False(policy.IsAllowed(IPAddress.Loopback, 8080));
    }

    [Fact]
    public void DynamicManagementPortIsDeniedAfterBindingAndUnavailableBeforeBinding()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TenonEdge:Http:Port"] = "0",
            ["TenonEdge:Outbound:LocalPorts:0"] = "12345",
            ["TenonEdge:Outbound:LocalPorts:1"] = "1883",
        }).Build();
        using var server = new ServerStub();
        var bound = new ServerAddressesFeature();
        server.Features.Set<IServerAddressesFeature>(bound);
        var policy = new HostOutboundAddressPolicy(configuration, ListenerSettings.Resolve(configuration), server, () => new(null, [], []));
        Assert.Throws<IOException>(() => policy.IsAllowed(IPAddress.Loopback, 1883));
        bound.Addresses.Add("http://127.0.0.1:12345");
        Assert.False(policy.IsAllowed(IPAddress.Loopback, 12345));
        Assert.True(policy.IsAllowed(IPAddress.Loopback, 1883));
    }

    [Fact]
    public void PublicRulesHaveNoEffectOnDifferentPortsOrMetadataAddresses()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TenonEdge:Outbound:Networks:0:Cidr"] = "0.0.0.0/0",
            ["TenonEdge:Outbound:Networks:0:Port"] = "443",
        }).Build();
        using var server = new ServerStub();
        var policy = new HostOutboundAddressPolicy(configuration, ListenerSettings.Resolve(configuration), server, () => new(null, [], []));
        Assert.True(policy.IsAllowed(IPAddress.Parse("8.8.8.8"), 443));
        Assert.False(policy.IsAllowed(IPAddress.Parse("8.8.8.8"), 80));
        Assert.False(policy.IsAllowed(IPAddress.Parse("169.254.169.254"), 443));
    }

    [Fact]
    public void InvalidDeploymentRulesFailClosedAsAccessDenial()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TenonEdge:Outbound:Networks:0:Cidr"] = "invalid-network",
            ["TenonEdge:Outbound:Networks:0:Port"] = "443",
        }).Build();
        using var server = new ServerStub();
        var policy = new HostOutboundAddressPolicy(configuration, ListenerSettings.Resolve(configuration), server, () => new(null, [], []));
        Assert.Throws<TenonEdge.Abstractions.Contracts.OutboundAccessDeniedException>(() => policy.IsAllowed(IPAddress.Parse("10.1.2.3"), 443));
    }

    private sealed class ServerStub : IServer
    {
        public IFeatureCollection Features { get; } = new FeatureCollection();
        public void Dispose() { }
        public Task StartAsync<TContext>(IHttpApplication<TContext> application, CancellationToken cancellationToken) where TContext : notnull => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
