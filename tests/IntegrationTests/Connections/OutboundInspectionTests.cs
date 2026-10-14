using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Security;
using TenonEdge.Host.Storage;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.Runtime.Diagnostics;

namespace TenonEdge.IntegrationTests.Connections;

public sealed class OutboundInspectionTests
{
    private const string Endpoint = "/api/edge-admin/v1/connections/inspect-outbound";
    private const string Policy = "/api/edge-admin/v1/system/outbound-policy";
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("Administrator", HttpStatusCode.OK)]
    [InlineData("Configurator", HttpStatusCode.OK)]
    [InlineData("Viewer", HttpStatusCode.Forbidden)]
    public async Task RolesAreEnforcedAndInspectionDoesNotChangeDraftPolicyOrAudit(string role, HttpStatusCode status)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "tester", ApiRequests.StrongPassword, Enum.Parse<UserRole>(role));
        using var client = await factory.CreateLoggedInClientAsync("tester", Token);
        var before = (await client.GetJsonAsync("/api/edge-admin/v1/connections", Token)).GetRawText();
        using var result = await client.PostAsJsonAsync(new Uri(Endpoint, UriKind.Relative), new OutboundInspectionRequest("203.0.113.5", 443), Token);
        Assert.Equal(status, result.StatusCode);
        if (status == HttpStatusCode.OK)
        {
            Assert.True(result.Headers.CacheControl!.NoStore);
            var address = Assert.Single((await result.ReadJsonAsync(Token)).GetProperty("addresses").EnumerateArray());
            Assert.Equal("publicGrantRequired", address.GetProperty("decision").GetString());
            Assert.Equal("203.0.113.5/32", address.GetProperty("grant").GetProperty("cidr").GetString());
        }
        Assert.Null(factory.Services.GetRequiredService<OutboundPolicyStore>().Read().Version);
        Assert.Equal(before, (await client.GetJsonAsync("/api/edge-admin/v1/connections", Token)).GetRawText());
        Assert.Empty(TestAccounts.Audit(factory.Services, "system.outboundPolicyChanged"));
    }

    [Fact]
    public async Task AnonymousCsrfAndSharedPreviewBudgetAreEnforced()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var request = new OutboundInspectionRequest("203.0.113.5", 443);
        using var anonymous = factory.CreateClient();
        using var denied = await anonymous.PostAsJsonAsync(new Uri(Endpoint, UriKind.Relative), request, Token);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        TestAccounts.Create(factory.Services, "tester", ApiRequests.StrongPassword, UserRole.Configurator);
        using var client = await factory.CreateLoggedInClientAsync("tester", Token);
        var csrf = client.DefaultRequestHeaders.GetValues(ApiRequests.CsrfHeader).Single();
        client.DefaultRequestHeaders.Remove(ApiRequests.CsrfHeader);
        using var noCsrf = await client.PostAsJsonAsync(new Uri(Endpoint, UriKind.Relative), request, Token);
        Assert.Equal(HttpStatusCode.Forbidden, noCsrf.StatusCode);
        client.DefaultRequestHeaders.Add(ApiRequests.CsrfHeader, csrf);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(csrf)));
        var budget = factory.Services.GetRequiredService<PreviewOperationBudget>();
        using var first = budget.TryAcquire(key);
        using var second = budget.TryAcquire(key);
        using var busy = await client.PostAsJsonAsync(new Uri(Endpoint, UriKind.Relative), request, Token);
        Assert.Equal(HttpStatusCode.TooManyRequests, busy.StatusCode);
    }

    [Fact]
    public async Task MixedDnsAnswersUseOneSnapshotOnlyPublicAddressesGetHostGrantsAndConfirmationIsVersionBound()
    {
        using var data = TemporaryDirectory.Create();
        var calls = 0;
        await using var factory = Factory(data.Path, (_, _) =>
        {
            calls++;
            return Task.FromResult<IPAddress[]>([IPAddress.Parse("203.0.113.5"), IPAddress.Parse("::ffff:203.0.113.5"),
                IPAddress.Parse("2001:db8::5"), IPAddress.Parse("10.254.254.254"), IPAddress.Loopback, IPAddress.Parse("169.254.169.254")]);
        });
        TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        using var client = await factory.CreateLoggedInClientAsync("admin", Token);
        var view = await Inspect(client, "fixture.invalid", 443);
        Assert.Equal(1, calls);
        var addresses = view.GetProperty("addresses").EnumerateArray().ToArray();
        Assert.Equal(5, addresses.Length);
        Assert.Equal("2001:db8::5/128", addresses[1].GetProperty("grant").GetProperty("cidr").GetString());
        Assert.Equal("allowed", addresses[2].GetProperty("decision").GetString());
        Assert.Equal("localPortRequired", addresses[3].GetProperty("decision").GetString());
        Assert.Equal("forbidden", addresses[4].GetProperty("decision").GetString());
        Assert.All(addresses.Skip(2), row => Assert.Equal(JsonValueKind.Null, row.GetProperty("grant").ValueKind));
        var request = new OutboundPolicyRequest([new("203.0.113.5/32", 443), new("2001:db8::5/128", 443)], []);
        using var write = await client.PutAsJsonAsync(new Uri(Policy, UriKind.Relative), request, Token);
        Assert.Equal(HttpStatusCode.OK, write.StatusCode);
        using var stale = await client.PutAsJsonAsync(new Uri(Policy, UriKind.Relative), request, Token);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var after = await Inspect(client, "fixture.invalid", 443);
        Assert.All(after.GetProperty("addresses").EnumerateArray().Take(2), row => Assert.Equal("allowed", row.GetProperty("decision").GetString()));
        var policy = factory.Services.GetRequiredService<IOutboundAddressPolicy>();
        Assert.Same(factory.Services.GetRequiredService<HostOutboundAddressPolicy>(), policy);
        Assert.True(policy.IsAllowed(IPAddress.Parse("203.0.113.5"), 443));
        Assert.False(policy.IsAllowed(IPAddress.Parse("203.0.113.6"), 443));
        Assert.False(policy.IsAllowed(IPAddress.Parse("203.0.113.5"), 444));
        Assert.Single(TestAccounts.Audit(factory.Services, "system.outboundPolicyChanged"));
    }

    [Fact]
    public async Task LocalAndManagementTargetsNeverReceiveNetworkGrantAdviceAndInspectionDoesNotOpenTcp()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        await using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        using var client = await factory.CreateLoggedInClientAsync("admin", Token);
        var local = await Inspect(client, "127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port);
        Assert.Equal("localPortRequired", local.GetProperty("addresses")[0].GetProperty("decision").GetString());
        Assert.False(listener.Pending());
        var management = await Inspect(client, "localhost", 8080); // Real OS DNS/hosts lookup, not a resolver fixture.
        Assert.All(management.GetProperty("addresses").EnumerateArray(), row =>
        {
            Assert.Equal("forbidden", row.GetProperty("decision").GetString());
            Assert.Equal(JsonValueKind.Null, row.GetProperty("grant").ValueKind);
        });
    }

    [Theory]
    [InlineData("https://example.com", 443)]
    [InlineData("user@example.com", 443)]
    [InlineData("example.com/path", 443)]
    [InlineData(" example.com", 443)]
    [InlineData("example.com", 0)]
    [InlineData("example.com", 65536)]
    public async Task InvalidTargetsAreRejectedBeforeDns(string host, int port)
    {
        using var data = TemporaryDirectory.Create();
        await using var factory = Factory(data.Path, (_, _) => throw new InvalidOperationException("DNS must not run"));
        TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        using var client = await factory.CreateLoggedInClientAsync("admin", Token);
        using var response = await client.PostAsJsonAsync(new Uri(Endpoint, UriKind.Relative), new OutboundInspectionRequest(host, port), Token);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData(0, HttpStatusCode.ServiceUnavailable)]
    [InlineData(32, HttpStatusCode.OK)]
    [InlineData(33, HttpStatusCode.RequestEntityTooLarge)]
    public async Task EmptyAndOversizedDnsAnswersDoNotReturnPartialAdvice(int count, HttpStatusCode status)
    {
        using var data = TemporaryDirectory.Create();
        await using var factory = Factory(data.Path, (_, _) => Task.FromResult(Enumerable.Repeat(IPAddress.Parse("203.0.113.5"), count).ToArray()));
        TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        using var client = await factory.CreateLoggedInClientAsync("admin", Token);
        using var response = await client.PostAsJsonAsync(new Uri(Endpoint, UriKind.Relative), new OutboundInspectionRequest("fixture.invalid", 443), Token);
        Assert.Equal(status, response.StatusCode);
        Assert.Null(factory.Services.GetRequiredService<OutboundPolicyStore>().Read().Version);
    }

    [Fact]
    public async Task MissingAndUnknownFieldsAreRejectedWithoutWritingPolicy()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        using var client = await factory.CreateLoggedInClientAsync("admin", Token);
        foreach (var body in new[] { "{}", "{\"host\":\"203.0.113.5\"}", "{\"host\":\"203.0.113.5\",\"port\":443,\"networks\":[]}" })
        {
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var invalid = await client.PostAsync(Endpoint, content, Token);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }
        // TestServer has no IHttpMaxRequestBodySizeFeature. The 4 KiB / +1 boundary is exercised by native browser fetch against real Kestrel.
        Assert.Null(factory.Services.GetRequiredService<OutboundPolicyStore>().Read().Version);
        Assert.Empty(TestAccounts.Audit(factory.Services, "system.outboundPolicyChanged"));
    }

    [Fact]
    public async Task DnsTimeoutIsBoundedAndReleasesTheAdmissionLease()
    {
        using var data = TemporaryDirectory.Create();
        var clock = new FakeTimeProvider();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var factory = Factory(data.Path, async (_, token) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return [];
        }, clock);
        TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        using var client = await factory.CreateLoggedInClientAsync("admin", Token);
        var pending = client.PostAsJsonAsync(new Uri(Endpoint, UriKind.Relative), new OutboundInspectionRequest("fixture.invalid", 443), Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        clock.Advance(TimeSpan.FromSeconds(5));
        using var response = await pending;
        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(client.DefaultRequestHeaders.GetValues(ApiRequests.CsrfHeader).Single())));
        var budget = factory.Services.GetRequiredService<PreviewOperationBudget>();
        using var first = budget.TryAcquire(key);
        using var second = budget.TryAcquire(key);
        Assert.NotNull(first);
        Assert.NotNull(second);
    }

    [Fact]
    public async Task InvalidDeploymentAndUnreadableStoredPolicyFailClosed()
    {
        using var data = TemporaryDirectory.Create();
        await using var factory = new FixedDataDirectoryApplicationFactory(data.Path,
            new Dictionary<string, string> { ["TenonEdge:Outbound:Networks:0:Cidr"] = "invalid" });
        TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        using var client = await factory.CreateLoggedInClientAsync("admin", Token);
        using var invalid = await client.PostAsJsonAsync(new Uri(Endpoint, UriKind.Relative), new OutboundInspectionRequest("203.0.113.5", 443), Token);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, invalid.StatusCode);
        // Separate host exercises persistent-read failure, rather than hiding it behind invalid deployment.
        await using var other = new TenonEdgeApplicationFactory();
        TestAccounts.Create(other.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        using var otherClient = await other.CreateLoggedInClientAsync("admin", Token);
        other.Services.GetRequiredService<ConfigDatabase>().Client.Ado.ExecuteCommand("DROP TABLE outbound_policy");
        using var unreadable = await otherClient.PostAsJsonAsync(new Uri(Endpoint, UriKind.Relative), new OutboundInspectionRequest("203.0.113.5", 443), Token);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, unreadable.StatusCode);
    }

    private static FixedDataDirectoryApplicationFactory Factory(string path, Func<string, CancellationToken, Task<IPAddress[]>> resolver, TimeProvider? clock = null) =>
        new(path, configureServices: services => services.AddScoped(provider => new OutboundPolicyInspector(
            provider.GetRequiredService<HostOutboundAddressPolicy>(), clock ?? TimeProvider.System, resolver)));

    private static async Task<JsonElement> Inspect(HttpClient client, string host, int port)
    {
        using var response = await client.PostAsJsonAsync(new Uri(Endpoint, UriKind.Relative), new OutboundInspectionRequest(host, port), Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.ReadJsonAsync(Token);
    }
}
