using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Host.Features.Deliveries;
using TenonEdge.Host.Security;
using TenonEdge.Host.Storage;
using TenonEdge.IntegrationTests.Infrastructure;

namespace TenonEdge.IntegrationTests.Deliveries;

public sealed class OutputControlTests
{
    private const string Endpoint = "/api/edge-admin/v1/connections/1/delivery-control";
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("Viewer", HttpStatusCode.Forbidden)]
    [InlineData("Configurator", HttpStatusCode.Forbidden)]
    [InlineData("Administrator", HttpStatusCode.OK)]
    public async Task OnlyAdministratorCanConfirmPause(string role, HttpStatusCode expected)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "operator", ApiRequests.StrongPassword, Enum.Parse<UserRole>(role));
        using var client = await factory.CreateLoggedInClientAsync("operator", Token);
        using var write = await client.PutAsJsonAsync(new Uri(Endpoint, UriKind.Relative), new { paused = true, confirmed = true }, Token);
        Assert.Equal(expected, write.StatusCode);
        using var read = await client.GetAsync(new Uri(Endpoint, UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(expected == HttpStatusCode.OK, (await read.ReadJsonAsync(Token)).GetProperty("manuallyPaused").GetBoolean());
        Assert.Equal(expected == HttpStatusCode.OK ? 1 : 0, TestAccounts.Audit(factory.Services, "delivery.pause").Count);
    }

    [Fact]
    public async Task AnonymousCsrfAndUnconfirmedRequestsCannotChangeState()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        using var anonymous = factory.CreateClient();
        using var denied = await anonymous.PutAsJsonAsync(new Uri(Endpoint, UriKind.Relative), new { paused = true, confirmed = true }, Token);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        using var admin = await factory.CreateLoggedInClientAsync("admin", Token);
        using var unconfirmed = await admin.PutAsJsonAsync(new Uri(Endpoint, UriKind.Relative), new { paused = true }, Token);
        Assert.Equal(HttpStatusCode.BadRequest, unconfirmed.StatusCode);
        admin.DefaultRequestHeaders.Remove(ApiRequests.CsrfHeader);
        using var csrf = await admin.PutAsJsonAsync(new Uri(Endpoint, UriKind.Relative), new { paused = true, confirmed = true }, Token);
        Assert.Equal(HttpStatusCode.Forbidden, csrf.StatusCode);
        Assert.False(factory.Services.GetRequiredService<OutputControlStore>().Read(1).ManuallyPaused);
    }

    [Fact]
    public async Task PauseSurvivesHostRestartAndStaleResumeIsRejected()
    {
        using var directory = TemporaryDirectory.Create();
        string version;
        await using (var factory = new FixedDataDirectoryApplicationFactory(directory.Path))
        {
            TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
            using var client = await factory.CreateLoggedInClientAsync("admin", Token);
            using var result = await client.PutAsJsonAsync(new Uri(Endpoint, UriKind.Relative), new { paused = true, confirmed = true }, Token);
            Assert.Equal(HttpStatusCode.OK, result.StatusCode);
            version = (await result.ReadJsonAsync(Token)).GetProperty("resumeVersion").GetString()!;
        }

        await using var restarted = new FixedDataDirectoryApplicationFactory(directory.Path);
        using var admin = await restarted.CreateLoggedInClientAsync("admin", Token);
        var store = restarted.Services.GetRequiredService<OutputControlStore>();
        Assert.True(store.Read(1).ManuallyPaused);
        Assert.Equal(version, store.Read(1).ResumeVersion);
        using var stale = await admin.PutAsJsonAsync(new Uri(Endpoint, UriKind.Relative), new { paused = false, confirmed = true, expectedVersion = "old" }, Token);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.True(store.Read(1).ManuallyPaused);
        using var resume = await admin.PutAsJsonAsync(new Uri(Endpoint, UriKind.Relative), new { paused = false, confirmed = true, expectedVersion = version }, Token);
        Assert.Equal(HttpStatusCode.OK, resume.StatusCode);
        Assert.False(store.Read(1).ManuallyPaused);
        Assert.Single(TestAccounts.Audit(restarted.Services, "delivery.pause"));
        Assert.Single(TestAccounts.Audit(restarted.Services, "delivery.resume"));
    }

    [Fact]
    public async Task QueueIsolationStartupFailureKeepsManagementAliveAndReadinessFalse()
    {
        using var directory = TemporaryDirectory.Create();
        Directory.CreateDirectory(Path.Combine(directory.Path, "queue.db"));
        await using var factory = new FixedDataDirectoryApplicationFactory(directory.Path);
        using var client = factory.CreateClient();
        using var health = await client.GetAsync(new Uri("/healthz", UriKind.Relative), Token);
        using var ready = await client.GetAsync(new Uri("/readyz", UriKind.Relative), Token);
        using var setup = await client.GetAsync(new Uri("/api/edge-admin/v1/setup", UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
    }

    [Fact]
    public async Task AuditFailureRollsBackPauseInSameTransaction()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        using var admin = await factory.CreateLoggedInClientAsync("admin", Token);
        var db = factory.Services.GetRequiredService<ConfigDatabase>();
        db.Client.Ado.ExecuteCommand("CREATE TRIGGER fail_pause_audit BEFORE INSERT ON audit_log WHEN NEW.action = 'delivery.pause' BEGIN SELECT RAISE(ABORT, 'audit failure'); END;");
        using var response = await admin.PutAsJsonAsync(new Uri(Endpoint, UriKind.Relative), new { paused = true, confirmed = true }, Token);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.False(factory.Services.GetRequiredService<OutputControlStore>().Read(1).ManuallyPaused);
        Assert.Empty(TestAccounts.Audit(factory.Services, "delivery.pause"));
    }
}
