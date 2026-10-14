using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Host.Bootstrap;
using TenonEdge.Host.Features.Audit;
using TenonEdge.Host.Features.SystemInfo;
using TenonEdge.Host.Features.Tokens;
using TenonEdge.Host.Security;
using TenonEdge.Host.Storage;
using TenonEdge.Host.Storage.Migrations;
using TenonEdge.IntegrationTests.Infrastructure;

namespace TenonEdge.IntegrationTests.SystemInfo;

public sealed class TrustedProxyTests
{
    private const string Endpoint = "/api/edge-admin/v1/system/trusted-proxies";
    private const string Action = "system.trustedProxiesChanged";
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("Administrator", HttpStatusCode.OK)]
    [InlineData("Configurator", HttpStatusCode.Forbidden)]
    [InlineData("Viewer", HttpStatusCode.Forbidden)]
    public async Task EveryoneReadsButOnlyAdministratorConfirmsTrust(string role, HttpStatusCode expected)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "operator", ApiRequests.StrongPassword, Enum.Parse<UserRole>(role));
        using var client = await factory.CreateLoggedInClientAsync("operator", Token);
        using var read = await client.GetAsync(Endpoint, Token);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.True(read.Headers.CacheControl!.NoStore);
        using var saved = await client.PutAsJsonAsync(Endpoint, new TrustedProxyRequest(["192.0.2.1"], null, true), Token);
        Assert.Equal(expected, saved.StatusCode);
        Assert.Equal(expected == HttpStatusCode.OK ? 1 : 0, TestAccounts.Audit(factory.Services, Action).Count);
    }

    [Fact]
    public async Task MissingConfirmationCsrfUnknownFieldsAndInvalidNetworksNeverChangeTrust()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        using var anonymous = factory.CreateClient();
        using var denied = await anonymous.GetAsync(Endpoint, Token);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        using var admin = await Admin(factory);
        object[] invalid = [new { networks = Array.Empty<string>(), confirmed = true },
            new { networks = Array.Empty<string>(), expectedVersion = (string?)null, confirmed = false },
            new { networks = Array.Empty<string>(), expectedVersion = (string?)null, confirmed = true, deployment = Array.Empty<string>() },
            new { networks = (string[]?)null, expectedVersion = (string?)null, confirmed = true }];
        foreach (var body in invalid)
        {
            using var response = await admin.PutAsJsonAsync(Endpoint, body, Token);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        foreach (var networks in new[] { new[] { "proxy.example.test" }, ["192.0.2.1:443"], [""], ["192.0.2.1/33"], Enumerable.Repeat("192.0.2.1", 1001).ToArray() })
        {
            using var response = await admin.PutAsJsonAsync(Endpoint, new TrustedProxyRequest(networks, null, true), Token);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        admin.DefaultRequestHeaders.Remove(ApiRequests.CsrfHeader);
        using var missing = await admin.PutAsJsonAsync(Endpoint, new TrustedProxyRequest(["192.0.2.1"], null, true), Token);
        Assert.Equal(HttpStatusCode.Forbidden, missing.StatusCode);
        Assert.Null(factory.Services.GetRequiredService<TrustedProxyStore>().Read().Version);
        Assert.Empty(TestAccounts.Audit(factory.Services, Action));
    }

    [Fact]
    public async Task NormalizationVersionAuditRollbackAndDeploymentUnionPersistWithoutChangingIdentity()
    {
        using var data = TemporaryDirectory.Create();
        var settings = new Dictionary<string, string> { [TrustedProxies.ConfigurationKey + ":0"] = "192.0.2.9" };
        TrustedProxyState last;
        string node;
        await using (var factory = new FixedDataDirectoryApplicationFactory(data.Path, settings))
        {
            using var admin = await Admin(factory);
            node = factory.Services.GetRequiredService<GatewayIdentity>().NodeId;
            using var saved = await admin.PutAsJsonAsync(Endpoint, new TrustedProxyRequest([" 192.0.2.1 ", "192.0.2.1/32", "2001:db8::/64"], null, true), Token);
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
            last = (await saved.Content.ReadFromJsonAsync<TrustedProxyState>(Token))!;
            Assert.Equal(["192.0.2.1/32", "2001:db8::/64"], last.Networks);
            Assert.Equal(["192.0.2.9/32"], last.Deployment);
            using var same = await admin.PutAsJsonAsync(Endpoint, new TrustedProxyRequest(["2001:db8::/64", "192.0.2.1"], last.Version, true), Token);
            Assert.Equal(HttpStatusCode.OK, same.StatusCode);
            Assert.Equal(last.Version, (await same.Content.ReadFromJsonAsync<TrustedProxyState>(Token))!.Version);
            using var stale = await admin.PutAsJsonAsync(Endpoint, new TrustedProxyRequest([], null, true), Token);
            Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
            var database = factory.Services.GetRequiredService<ConfigDatabase>();
            database.Client.Ado.ExecuteCommand($"CREATE TRIGGER refuse_proxy_audit BEFORE INSERT ON audit_log WHEN NEW.action='{Action}' BEGIN SELECT RAISE(ABORT, 'fixture'); END;");
            using var failed = await admin.PutAsJsonAsync(Endpoint, new TrustedProxyRequest([], last.Version, true), Token);
            Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
            Assert.Equal(last.Version, factory.Services.GetRequiredService<TrustedProxyStore>().Read().Version);
            Assert.True(factory.Services.GetRequiredService<TrustedProxyStore>().Current.Proxies.Contains(IPAddress.Parse("192.0.2.1")));
            Assert.Single(TestAccounts.Audit(factory.Services, Action));
            database.Client.Ado.ExecuteCommand("DROP TRIGGER refuse_proxy_audit");
        }
        await using var restarted = new FixedDataDirectoryApplicationFactory(data.Path, settings);
        using var again = await restarted.CreateLoggedInClientAsync("admin", Token);
        var loaded = (await again.GetFromJsonAsync<TrustedProxyState>(Endpoint, Token))!;
        Assert.Equal(last.Version, loaded.Version);
        Assert.Equal(last.Networks, loaded.Networks);
        Assert.Equal(node, restarted.Services.GetRequiredService<GatewayIdentity>().NodeId);
        using var clear = await again.PutAsJsonAsync(Endpoint, new TrustedProxyRequest([], loaded.Version, true), Token);
        Assert.Equal(HttpStatusCode.OK, clear.StatusCode);
        var trust = restarted.Services.GetRequiredService<TrustedProxyStore>().Current.Proxies;
        Assert.False(trust.Contains(IPAddress.Parse("192.0.2.1")));
        Assert.True(trust.Contains(IPAddress.Parse("192.0.2.9")));
    }

    [Fact]
    public async Task AdministratorIsRecheckedInsideWriteAndCorruptPersistentTrustCannotBecomeEmpty()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var user = TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        var actor = new ManagementActor(user.Id, user.SecurityStamp, null);
        var store = factory.Services.GetRequiredService<TrustedProxyStore>();
        var state = store.Write(new(["192.0.2.1"], null, true), actor);
        TestAccounts.Update(factory.Services, "admin", account => account.Role = nameof(UserRole.Viewer));
        Assert.Throws<ApiTokenException>(() => store.Write(new([], state.Version, true), actor));
        Assert.Equal(state.Version, store.Read().Version);
        var database = factory.Services.GetRequiredService<ConfigDatabase>();
        database.Client.Ado.ExecuteCommand("UPDATE trusted_proxy_rules SET networks_json='[null]' WHERE id=1");
        var loader = new TrustedProxyStore(database, factory.Services.GetRequiredService<AuditTrail>(),
            factory.Services.GetRequiredService<TrustedProxies>(), TimeProvider.System);
        Assert.Equal(HostExitCodes.ConfigStoreUnavailable, Assert.Throws<HostStartupException>(() => loader.Read()).ExitCode);
    }

    [Fact]
    public void MigrationFrom24KeepsQueueParameterValues()
    {
        using var store = TestConfigStore.Create();
        store.Initialize(ConfigMigrations.All.Where(m => m.Version <= 24).ToArray());
        store.Database.Client.Insertable(new QueueParameterRecord { Version = Guid.NewGuid().ToString("N"), ValuesJson = JsonSerializer.Serialize(QueueParameterValues.Default) }).ExecuteCommand();
        Assert.Equal(ConfigMigrations.All[^1].Version, store.CreateMigrator().Migrate());
        Assert.Empty(store.Database.Client.Queryable<TrustedProxyRecord>().ToList());
        Assert.Single(store.Database.Client.Queryable<QueueParameterRecord>().ToList());
    }

    private static Task<HttpClient> Admin(TenonEdgeApplicationFactory factory)
    {
        TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        return factory.CreateLoggedInClientAsync("admin", Token);
    }
}
