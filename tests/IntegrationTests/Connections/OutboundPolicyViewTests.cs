using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Security;
using TenonEdge.IntegrationTests.Infrastructure;

namespace TenonEdge.IntegrationTests.Connections;

public sealed class OutboundPolicyViewTests
{
    private const string Root = "/api/edge-admin/v1/system/outbound-policy";
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ReadSeparatesDeploymentGrantsAndRemovingWebGrantsCannotRevokeDeploymentRules()
    {
        using var data = TemporaryDirectory.Create();
        await using var factory = new FixedDataDirectoryApplicationFactory(data.Path, Settings("198.51.100.0/24", "8883"));
        TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        using var admin = await factory.CreateLoggedInClientAsync("admin", Token);
        var first = await admin.GetJsonAsync(Root, Token);
        Assert.Equal(JsonValueKind.Null, first.GetProperty("version").ValueKind);
        Assert.Empty(first.GetProperty("networks").EnumerateArray());
        Assert.Equal("198.51.100.0/24", Assert.Single(first.GetProperty("deployment").GetProperty("networks").EnumerateArray()).GetProperty("cidr").GetString());
        Assert.Equal(2883, Assert.Single(first.GetProperty("deployment").GetProperty("localPorts").EnumerateArray()).GetInt32());
        using var changed = await admin.PutAsJsonAsync(new Uri(Root, UriKind.Relative), new OutboundPolicyRequest([new("203.0.113.0/24", 443)], [1883]), Token);
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.True(changed.Headers.CacheControl!.NoStore);
        var version = (await changed.ReadJsonAsync(Token)).GetProperty("version").GetString();
        var policy = factory.Services.GetRequiredService<IOutboundAddressPolicy>();
        Assert.True(policy.IsAllowed(IPAddress.Parse("203.0.113.5"), 443));
        Assert.True(policy.IsAllowed(IPAddress.Parse("198.51.100.5"), 8883));
        using var cleared = await admin.PutAsJsonAsync(new Uri(Root, UriKind.Relative), new OutboundPolicyRequest([], [], version), Token);
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        Assert.False(policy.IsAllowed(IPAddress.Parse("203.0.113.5"), 443));
        Assert.True(policy.IsAllowed(IPAddress.Parse("198.51.100.5"), 8883));
        Assert.True(policy.IsAllowed(IPAddress.Loopback, 2883));
        Assert.False(policy.IsAllowed(IPAddress.Loopback, 1883));
        using var read = await admin.GetAsync(Root, Token);
        Assert.True(read.Headers.CacheControl!.NoStore);
        Assert.Equal(first.GetProperty("deployment").GetRawText(), (await read.ReadJsonAsync(Token)).GetProperty("deployment").GetRawText());
        Assert.Equal(2, TestAccounts.Audit(factory.Services, "system.outboundPolicyChanged").Count);
    }

    [Theory]
    [InlineData("invalid", "443")]
    [InlineData("203.0.113.0/24", "65536")]
    [InlineData("203.0.113.0/24", "invalid")]
    public async Task InvalidDeploymentIsExplicitlyUnknownAndFailsClosedInsteadOfAppearingEmpty(string cidr, string port)
    {
        using var data = TemporaryDirectory.Create();
        await using var factory = new FixedDataDirectoryApplicationFactory(data.Path, Settings(cidr, port));
        TestAccounts.Create(factory.Services, "viewer", ApiRequests.StrongPassword, UserRole.Viewer);
        using var viewer = await factory.CreateLoggedInClientAsync("viewer", Token);
        var view = await viewer.GetJsonAsync(Root, Token);
        Assert.Equal(JsonValueKind.Null, view.GetProperty("deployment").ValueKind);
        Assert.Empty(view.GetProperty("networks").EnumerateArray());
        Assert.Throws<OutboundAccessDeniedException>(() => factory.Services.GetRequiredService<IOutboundAddressPolicy>().IsAllowed(IPAddress.Parse("10.1.2.3"), 80));
        Assert.Empty(TestAccounts.Audit(factory.Services, "system.outboundPolicyChanged"));
    }

    [Fact]
    public async Task DeploymentFieldsCannotBeAccidentallyPostedBackAsIfEditable()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        using var admin = await factory.CreateLoggedInClientAsync("admin", Token);
        using var response = await admin.PutAsJsonAsync(new Uri(Root, UriKind.Relative), new { networks = Array.Empty<object>(), localPorts = Array.Empty<int>(), deployment = new { } }, Token);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(factory.Services.GetRequiredService<OutboundPolicyStore>().Read().Version);
    }

    private static Dictionary<string, string> Settings(string cidr, string port) => new()
    {
        ["TenonEdge:Outbound:Networks:0:Cidr"] = cidr,
        ["TenonEdge:Outbound:Networks:0:Port"] = port,
        ["TenonEdge:Outbound:LocalPorts:0"] = "2883",
    };
}
