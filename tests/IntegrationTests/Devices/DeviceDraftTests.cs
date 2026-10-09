using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Host.Features.Devices;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Security;
using TenonEdge.Host.Storage;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.Runtime.Deliveries;

namespace TenonEdge.IntegrationTests.Devices;

public sealed class DeviceDraftTests
{
    private const string Endpoint = "/api/edge-admin/v1/devices/";
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("Viewer", HttpStatusCode.Forbidden)]
    [InlineData("Configurator", HttpStatusCode.OK)]
    [InlineData("Administrator", HttpStatusCode.OK)]
    public async Task RolesReadDraftsButOnlyEditorsCanMutate(string role, HttpStatusCode expected)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "operator", ApiRequests.StrongPassword, Enum.Parse<UserRole>(role));
        using var client = await factory.CreateLoggedInClientAsync("operator", Token);
        using var read = await client.GetAsync(new Uri(Endpoint, UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        using var write = await client.PostJsonAsync(Endpoint, Request(), Token);
        Assert.Equal(expected, write.StatusCode);
        Assert.Equal(expected == HttpStatusCode.OK ? 1 : 0, TestAccounts.Audit(factory.Services, "device.draftCreated").Count);
        var store = factory.Services.GetRequiredService<DeviceDraftStore>();
        if (expected == HttpStatusCode.Forbidden)
        {
            var created = store.Write(null, Request(), "test", null);
            using var update = await client.PutAsJsonAsync(new Uri(Endpoint + created.Device.Id, UriKind.Relative), Request(created.Version), Token);
            Assert.Equal(HttpStatusCode.Forbidden, update.StatusCode);
            using var delete = await client.DeleteAsync(new Uri($"{Endpoint}{created.Device.Id}?expectedVersion={created.Version}", UriKind.Relative), Token);
            Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);
        }
    }

    [Fact]
    public async Task AnonymousAndCsrfFailuresHaveNoWrites()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        using var anonymous = factory.CreateClient();
        using var read = await anonymous.GetAsync(new Uri(Endpoint, UriKind.Relative), Token);
        using var denied = await anonymous.PostJsonAsync(Endpoint, Request(), Token);
        Assert.Equal(HttpStatusCode.Unauthorized, read.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        using var admin = await factory.CreateLoggedInClientAsync("admin", Token);
        admin.DefaultRequestHeaders.Remove(ApiRequests.CsrfHeader);
        using var csrf = await admin.PostJsonAsync(Endpoint, Request(), Token);
        Assert.Equal(HttpStatusCode.Forbidden, csrf.StatusCode);
        Assert.Empty(factory.Services.GetRequiredService<DeviceDraftStore>().Read().Devices);
    }

    [Fact]
    public async Task CrudPersistsAcrossRestartAndDoesNotChangeRuntimePointers()
    {
        using var directory = TemporaryDirectory.Create();
        long id;
        string version;
        await using (var factory = new FixedDataDirectoryApplicationFactory(directory.Path))
        {
            TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
            using var client = await factory.CreateLoggedInClientAsync("admin", Token);
            using var write = await client.PostJsonAsync(Endpoint, Request(), Token);
            Assert.Equal(HttpStatusCode.OK, write.StatusCode);
            var result = await write.ReadJsonAsync(Token);
            id = result.GetProperty("device").GetProperty("id").GetInt64();
            version = result.GetProperty("version").GetString()!;
            Assert.InRange(id, 1, 9007199254740991L);
            var state = factory.Services.GetRequiredService<ConfigDatabase>().Client.Queryable<SystemStateRecord>().InSingle(1);
            Assert.Null(state.PendingRevision);
            Assert.Null(state.EffectiveRevision);
            Assert.Null(state.PreviousRevision);
        }
        await using var restarted = new FixedDataDirectoryApplicationFactory(directory.Path);
        using var admin = await restarted.CreateLoggedInClientAsync("admin", Token);
        var store = restarted.Services.GetRequiredService<DeviceDraftStore>();
        Assert.Equal(version, store.Read().Version);
        Assert.Equal(id, Assert.Single(store.Read().Devices).Id);
        using var changed = await admin.PutAsJsonAsync(new Uri(Endpoint + id, UriKind.Relative), Request(version) with { Name = "updated", Enabled = false }, Token);
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        var updated = store.Read();
        Assert.False(updated.Devices[0].Enabled);
        using var deleted = await admin.DeleteAsync(new Uri($"{Endpoint}{id}?expectedVersion={updated.Version}", UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        Assert.Empty(store.Read().Devices);
        Assert.Single(TestAccounts.Audit(restarted.Services, "device.draftUpdated"));
        Assert.Single(TestAccounts.Audit(restarted.Services, "device.draftDeleted"));
    }

    [Fact]
    public async Task ConcurrentSavesAndStaleDeletesCannotLoseOtherEdits()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        using var client = await factory.CreateLoggedInClientAsync("admin", Token);
        var requests = await Task.WhenAll(client.PostJsonAsync(Endpoint, Request(), Token), client.PostJsonAsync(Endpoint, Request() with { Key = "other" }, Token));
        using var first = requests[0];
        using var second = requests[1];
        Assert.Single(requests, response => response.StatusCode == HttpStatusCode.OK);
        Assert.Single(requests, response => response.StatusCode == HttpStatusCode.Conflict);
        var store = factory.Services.GetRequiredService<DeviceDraftStore>();
        var snapshot = store.Read();
        var entry = Assert.Single(snapshot.Devices);
        using var stale = await client.DeleteAsync(new Uri($"{Endpoint}{entry.Id}?expectedVersion=0", UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(snapshot.Version, store.Read().Version);
        using var duplicate = await client.PostJsonAsync(Endpoint, Request(snapshot.Version) with { Key = entry.Key }, Token);
        Assert.Equal("device.duplicateKey", await duplicate.ReadProblemCodeAsync(Token));
        using var changedKey = await client.PutAsJsonAsync(new Uri(Endpoint + entry.Id, UriKind.Relative), Request(snapshot.Version) with { Key = "replacement" }, Token);
        Assert.Equal("device.immutableKey", await changedKey.ReadProblemCodeAsync(Token));
        Assert.Equal(snapshot.Version, store.Read().Version);
    }

    [Fact]
    public async Task AuditFailureRollsBackObjectAndGlobalDraftVersion()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        using var client = await factory.CreateLoggedInClientAsync("admin", Token);
        var db = factory.Services.GetRequiredService<ConfigDatabase>();
        db.Client.Ado.ExecuteCommand("CREATE TRIGGER fail_draft_audit BEFORE INSERT ON audit_log WHEN NEW.action = 'device.draftCreated' BEGIN SELECT RAISE(ABORT, 'audit failure'); END;");
        using var failed = await client.PostJsonAsync(Endpoint, Request(), Token);
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        var snapshot = factory.Services.GetRequiredService<DeviceDraftStore>().Read();
        Assert.Equal("0", snapshot.Version);
        Assert.Empty(snapshot.Devices);
    }

    private static DeviceDraftRequest Request(string version = "0") => new(version, "meter", "Meter", DeviceKind.Simulator);
}
