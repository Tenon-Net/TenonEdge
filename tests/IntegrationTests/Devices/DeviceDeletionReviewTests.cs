using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Host.Features.Configuration;
using TenonEdge.Host.Features.Devices;
using TenonEdge.Host.Features.Inputs;
using TenonEdge.Host.Features.Routes;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Security;
using TenonEdge.Host.Storage;
using TenonEdge.IntegrationTests.Infrastructure;

namespace TenonEdge.IntegrationTests.Devices;

public sealed class DeviceDeletionReviewTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static string Path(long id, string version) => $"/api/edge-admin/v1/devices/{id}/deletion-review?expectedVersion={version}";

    [Theory]
    [InlineData("Administrator")]
    [InlineData("Configurator")]
    [InlineData("Viewer")]
    public async Task AuthenticatedReviewListsDisabledRouteReferencesOnceWithoutBodiesOrSideEffects(string role)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var services = factory.Services;
        TestAccounts.Create(services, "operator", ApiRequests.StrongPassword, Enum.Parse<UserRole>(role));
        var devices = services.GetRequiredService<DeviceDraftStore>();
        var device = devices.Write(null, new("0", "meter", "Meter", DeviceKind.Simulator), "fixture", null);
        var input = services.GetRequiredService<InputDraftStore>().Write(device.Device.Id, null,
            new(device.Version, "read", "Read", Simulator: new(SampleJson: "{\"v\":\"private-sample\"}")), "fixture", null);
        var graph = new RouteGraph(1,
            [new("a", "input.simulator", 1, "private-node", JsonSerializer.SerializeToElement(new { sourceIds = new[] { input.Input.Id } })),
             new("b", "input.simulator", 1, "private-node", JsonSerializer.SerializeToElement(new { sourceIds = new[] { input.Input.Id } }))], [], new([]));
        var routes = services.GetRequiredService<RouteDraftStore>();
        var route = routes.Write(null, new(input.Version, "route", "Route name", false, graph, "private-description"), "fixture", null);
        using var anonymous = factory.CreateClient();
        using var denied = await anonymous.GetAsync(new Uri(Path(device.Device.Id, route.Version), UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        using var client = await factory.CreateLoggedInClientAsync("operator", Token);
        using var response = await client.GetAsync(new Uri(Path(device.Device.Id, route.Version), UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        var data = await response.ReadJsonAsync(Token);
        Assert.False(data.GetProperty("canDelete").GetBoolean());
        Assert.Equal(1, data.GetProperty("inputCount").GetInt32());
        var reference = Assert.Single(data.GetProperty("routes").EnumerateArray());
        Assert.Equal(route.Route.Id, reference.GetProperty("id").GetInt64());
        Assert.Equal("Route name", reference.GetProperty("name").GetString());
        Assert.Equal("route", reference.GetProperty("key").GetString());
        Assert.False(reference.GetProperty("enabled").GetBoolean());
        Assert.Empty(data.GetProperty("versions").EnumerateArray());
        Assert.DoesNotContain("private-", data.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(route.Version, devices.Read().Version);
        Assert.Empty(TestAccounts.Audit(services, "device.draftDeleted"));
        using var deletion = await client.DeleteAsync(new Uri($"/api/edge-admin/v1/devices/{device.Device.Id}?expectedVersion={route.Version}", UriKind.Relative), Token);
        Assert.Equal(role == "Viewer" ? HttpStatusCode.Forbidden : HttpStatusCode.Conflict, deletion.StatusCode);
        Assert.Single(devices.Read().Devices);
        var removedRoute = routes.Delete(route.Route.Id, route.Version, "fixture", null);
        using var stale = await client.GetAsync(new Uri(Path(device.Device.Id, route.Version), UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var clear = await client.GetJsonAsync(Path(device.Device.Id, removedRoute.Version), Token);
        Assert.True(clear.GetProperty("canDelete").GetBoolean());
        Assert.Empty(clear.GetProperty("routes").EnumerateArray());
    }

    [Theory]
    [InlineData("effective")]
    [InlineData("previous")]
    [InlineData("pending")]
    public async Task RetainedPointersBlockEvenAfterAClearReviewWithoutChangingDraftVersion(string position)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var devices = factory.Services.GetRequiredService<DeviceDraftStore>();
        var device = devices.Write(null, new("0", "meter", "Meter", DeviceKind.Simulator), "fixture", null);
        Assert.True(devices.ReviewDeletion(device.Device.Id, device.Version).CanDelete);
        Retain(factory, position);
        var review = devices.ReviewDeletion(device.Device.Id, device.Version);
        Assert.False(review.CanDelete);
        Assert.Equal(device.Version, review.Version);
        var reference = Assert.Single(review.Versions);
        Assert.Equal(1, reference.Revision);
        Assert.Equal([position], reference.Positions);
        Assert.Equal(["input.simulator/meter/read"], reference.Units);
        Assert.Equal("device.referenced", Assert.Throws<DraftWriteException>(() => devices.Delete(device.Device.Id, review.Version, "fixture", null)).Code);
        Assert.Single(devices.Read().Devices);
        Assert.Empty(TestAccounts.Audit(factory.Services, "device.draftDeleted"));
    }

    [Fact]
    public async Task SharedPointersDeduplicateRevisionAndUnreadableOrMissingSnapshotsNeverAuthorizeDeletion()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var services = factory.Services;
        TestAccounts.Create(services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        var devices = services.GetRequiredService<DeviceDraftStore>();
        var device = devices.Write(null, new("0", "meter", "Meter", DeviceKind.Simulator), "fixture", null);
        Retain(factory, "all");
        Assert.Equal(3, Assert.Single(devices.ReviewDeletion(device.Device.Id, device.Version).Versions).Positions.Length);
        using var client = await factory.CreateLoggedInClientAsync("admin", Token);
        var database = services.GetRequiredService<ConfigDatabase>();
        foreach (var missing in new[] { false, true })
        {
            database.InTransaction(db => missing
                ? db.Deleteable<RuntimeSnapshotRecord>().Where(row => row.Revision == 1).ExecuteCommand()
                : db.Updateable<RuntimeSnapshotRecord>().SetColumns(row => row.ContentHash == "bad").Where(row => row.Revision == 1).ExecuteCommand());
            using var response = await client.GetAsync(new Uri(Path(device.Device.Id, device.Version), UriKind.Relative), Token);
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal("device.referencesUnavailable", await response.ReadProblemCodeAsync(Token));
            Assert.Single(devices.Read().Devices);
        }
    }

    [Fact]
    public async Task ClearReviewDoesNotReplaceTransactionalVersionAndAuditChecks()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var devices = factory.Services.GetRequiredService<DeviceDraftStore>();
        var device = devices.Write(null, new("0", "meter", "Meter", DeviceKind.Simulator), "fixture", null);
        var review = devices.ReviewDeletion(device.Device.Id, device.Version);
        var changed = devices.Write(device.Device.Id, new(device.Version, "meter", "Changed", DeviceKind.Simulator), "fixture", null);
        Assert.True(review.CanDelete);
        Assert.Equal("configuration.draftConflict", Assert.Throws<DraftWriteException>(() => devices.Delete(device.Device.Id, review.Version, "fixture", null)).Code);
        var current = devices.ReviewDeletion(device.Device.Id, changed.Version);
        Assert.Equal("Changed", current.Name);
        devices.Delete(device.Device.Id, current.Version, "fixture", null);
        Assert.Empty(devices.Read().Devices);
        Assert.Single(TestAccounts.Audit(factory.Services, "device.draftDeleted"));
        Assert.Throws<DraftWriteException>(() => devices.ReviewDeletion(device.Device.Id, devices.Read().Version));
        Assert.Throws<ArgumentException>(() => devices.ReviewDeletion(0, current.Version));
        Assert.Throws<ArgumentException>(() => devices.ReviewDeletion(device.Device.Id, new string('v', 65)));
    }

    private static void Retain(TenonEdgeApplicationFactory factory, string position)
    {
        var plan = RuntimePlanDocument.Parse("""
            {"schemaVersion":1,"units":[{"kind":"input.simulator","key":"meter/read","configuration":{"periodMs":5000}}]}
            """u8);
        factory.Services.GetRequiredService<ConfigDatabase>().InTransaction(db =>
        {
            db.Insertable(new RuntimeSnapshotRecord
            {
                Revision = 1,
                SchemaVersion = 1,
                Content = plan.CanonicalText,
                ContentHash = plan.ContentHash,
                Origin = RuntimeSnapshotOrigins.Seed,
                State = RuntimeSnapshotStates.Effective,
                CreatedBy = "fixture"
            }).ExecuteCommand();
            var state = db.Queryable<SystemStateRecord>().InSingle(1);
            if (position is "all" or "effective")
            { state.EffectiveRevision = 1; }
            if (position is "all" or "previous")
            { state.PreviousRevision = 1; }
            if (position is "all" or "pending")
            { state.PendingRevision = 1; }
            return db.Updateable(state).ExecuteCommand();
        });
    }
}
