using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Security;
using TenonEdge.Host.Storage;
using TenonEdge.IntegrationTests.Infrastructure;

namespace TenonEdge.IntegrationTests.Connections;

public sealed class OutboundPolicyTests
{
    private const string Endpoint = "/api/edge-admin/v1/system/outbound-policy";
    private const string Action = "system.outboundPolicyChanged";
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("Viewer", HttpStatusCode.Forbidden)]
    [InlineData("Configurator", HttpStatusCode.Forbidden)]
    [InlineData("Administrator", HttpStatusCode.OK)]
    public async Task OnlyAdministratorCanWriteAndEveryRoleCanRead(string role, HttpStatusCode expected)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "operator", ApiRequests.StrongPassword, Enum.Parse<UserRole>(role));
        using var client = await factory.CreateLoggedInClientAsync("operator", Token);
        using var write = await client.PutAsJsonAsync(new Uri(Endpoint, UriKind.Relative), Request(), Token);
        Assert.Equal(expected, write.StatusCode);
        using var read = await client.GetAsync(new Uri(Endpoint, UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(expected == HttpStatusCode.OK ? 1 : 0, TestAccounts.Audit(factory.Services, Action).Count);
        var policy = factory.Services.GetRequiredService<IOutboundAddressPolicy>();
        Assert.Equal(expected == HttpStatusCode.OK, policy.IsAllowed(IPAddress.Parse("203.0.113.9"), 443));
        Assert.False(policy.IsAllowed(IPAddress.Parse("203.0.113.9"), 80));
        Assert.False(policy.IsAllowed(IPAddress.Loopback, 8080));
    }

    [Fact]
    public async Task AnonymousAndMissingCsrfCannotChangePolicy()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        using var anonymous = factory.CreateClient();
        using var denied = await anonymous.PutAsJsonAsync(new Uri(Endpoint, UriKind.Relative), Request(), Token);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        using var read = await anonymous.GetAsync(new Uri(Endpoint, UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.Unauthorized, read.StatusCode);
        TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        using var admin = await factory.CreateLoggedInClientAsync("admin", Token);
        admin.DefaultRequestHeaders.Remove(ApiRequests.CsrfHeader);
        using var csrf = await admin.PutAsJsonAsync(new Uri(Endpoint, UriKind.Relative), Request(), Token);
        Assert.Equal(HttpStatusCode.Forbidden, csrf.StatusCode);
        Assert.Null(factory.Services.GetRequiredService<OutboundPolicyStore>().Read().Version);
    }

    [Fact]
    public async Task PolicySurvivesRestartAndStaleReplacementCannotOverwriteIt()
    {
        using var directory = TemporaryDirectory.Create();
        string version;
        await using (var factory = new FixedDataDirectoryApplicationFactory(directory.Path))
        {
            TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
            using var admin = await factory.CreateLoggedInClientAsync("admin", Token);
            using var write = await admin.PutAsJsonAsync(new Uri(Endpoint, UriKind.Relative), Request(), Token);
            Assert.Equal(HttpStatusCode.OK, write.StatusCode);
            version = (await write.ReadJsonAsync(Token)).GetProperty("version").GetString()!;
        }
        await using var restarted = new FixedDataDirectoryApplicationFactory(directory.Path);
        using var client = await restarted.CreateLoggedInClientAsync("admin", Token);
        var store = restarted.Services.GetRequiredService<OutboundPolicyStore>();
        Assert.Equal(version, store.Read().Version);
        var policy = restarted.Services.GetRequiredService<IOutboundAddressPolicy>();
        Assert.True(policy.IsAllowed(IPAddress.Loopback, 1883));
        using var stale = await client.PutAsJsonAsync(new Uri(Endpoint, UriKind.Relative), new OutboundPolicyRequest([], [], "old"), Token);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.True(policy.IsAllowed(IPAddress.Loopback, 1883));
        using var remove = await client.PutAsJsonAsync(new Uri(Endpoint, UriKind.Relative), new OutboundPolicyRequest([], [], version), Token);
        Assert.Equal(HttpStatusCode.OK, remove.StatusCode);
        Assert.False(policy.IsAllowed(IPAddress.Loopback, 1883));
        Assert.Equal(2, TestAccounts.Audit(restarted.Services, Action).Count);
    }

    [Fact]
    public async Task AuditFailureRollsBackThePermissionsChange()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        using var admin = await factory.CreateLoggedInClientAsync("admin", Token);
        var database = factory.Services.GetRequiredService<ConfigDatabase>();
        database.Client.Ado.ExecuteCommand($"CREATE TRIGGER fail_outbound_audit BEFORE INSERT ON audit_log WHEN NEW.action = '{Action}' BEGIN SELECT RAISE(ABORT, 'audit failure'); END;");
        using var failed = await admin.PutAsJsonAsync(new Uri(Endpoint, UriKind.Relative), Request(), Token);
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.Null(factory.Services.GetRequiredService<OutboundPolicyStore>().Read().Version);
        Assert.False(factory.Services.GetRequiredService<IOutboundAddressPolicy>().IsAllowed(IPAddress.Loopback, 1883));
        Assert.Empty(TestAccounts.Audit(factory.Services, Action));
    }

    [Theory]
    [InlineData("invalid", 443)]
    [InlineData("0.0.0.0/0", 0)]
    [InlineData("::/0", 65536)]
    public async Task InvalidRuleReturnsValidationWithoutChangingState(string cidr, int port)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        using var admin = await factory.CreateLoggedInClientAsync("admin", Token);
        using var invalid = await admin.PutAsJsonAsync(new Uri(Endpoint, UriKind.Relative), new OutboundPolicyRequest([new(cidr, port)], []), Token);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Null(factory.Services.GetRequiredService<OutboundPolicyStore>().Read().Version);
        Assert.Empty(TestAccounts.Audit(factory.Services, Action));
    }

    [Theory]
    [InlineData("[null]", "[]")]
    [InlineData("null", "[]")]
    [InlineData("[]", "null")]
    [InlineData("[{\"Cidr\":\"invalid\",\"Port\":443}]", "[]")]
    [InlineData("[]", "[0]")]
    [InlineData("{", "[]")]
    public async Task MalformedPersistedRulesFailClosedAsRetryableReadFailure(string networks, string localPorts)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var database = factory.Services.GetRequiredService<ConfigDatabase>();
        database.Client.Insertable(new OutboundPolicyRecord
        {
            Version = "corrupt", Networks = networks, LocalPorts = localPorts
        }).ExecuteCommand();
        var policy = factory.Services.GetRequiredService<IOutboundAddressPolicy>();
        var exception = Assert.Throws<IOException>(() => policy.IsAllowed(IPAddress.Parse("10.20.30.40"), 1883));
        Assert.IsType<System.Text.Json.JsonException>(exception.InnerException);
        Assert.Empty(TestAccounts.Audit(factory.Services, Action));
    }

    private static OutboundPolicyRequest Request() => new([new("203.0.113.0/24", 443)], [1883, 8080]);
}
