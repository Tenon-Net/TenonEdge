using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Deliveries;
using TenonEdge.Host.Features.Devices;
using TenonEdge.Host.Features.Inputs;
using TenonEdge.Host.Features.Routes;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Security;
using TenonEdge.Host.Storage;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.IntegrationTests.Runtime;

namespace TenonEdge.IntegrationTests.Routes;

public sealed class RouteRuntimeEndpointTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static string Path(long route, string version) => $"/api/edge-admin/v1/routes/{route}/runtime?expectedVersion={Uri.EscapeDataString(version)}";

    [Theory]
    [InlineData("Administrator")]
    [InlineData("Configurator")]
    [InlineData("Viewer")]
    public async Task AuthenticatedRolesSeeActualPipelineMetadataButNeverMessageBodies(string roleName)
    {
        var role = Enum.Parse<UserRole>(roleName);
        await using var factory = new TenonEdgeApplicationFactory();
        var services = factory.Services;
        TestAccounts.Create(services, "operator", ApiRequests.StrongPassword, role);
        var connection = services.GetRequiredService<ConnectionDraftStore>().Write(null,
            new("0", "http", "HTTP", ConnectionKind.Http, Http: new("http://localhost/")), "fixture", null);
        var device = services.GetRequiredService<DeviceDraftStore>().Write(null,
            new(connection.Version, "meter", "Meter", DeviceKind.Simulator), "fixture", null);
        var source = services.GetRequiredService<InputDraftStore>().Write(device.Device.Id, null,
            new(device.Version, "sample", "Sample", Simulator: new(PeriodMs: 3600000, SampleJson: "{\"value\":42,\"privateMessage\":\"not-for-viewer\"}")), "fixture", null);
        var graph = new RouteGraph(1,
            [new("input", "input.simulator", 1, "Input", JsonSerializer.SerializeToElement(new { sourceIds = new[] { source.Input.Id } })),
             new("map", "transform.map", 1, "Map", JsonSerializer.Deserialize<JsonElement>("""{"rules":[{"target":"value","type":"integer","sourceField":"value"}]}""")),
             new("send", "output.http", 1, "Send", JsonSerializer.SerializeToElement(new { connectionId = connection.Connection.Id }))],
            [new("input", "map"), new("map", "send")], new([]));
        var drafts = services.GetRequiredService<RouteDraftStore>();
        var route = drafts.Write(null, new(source.Version, "route", "Route", true, graph), "fixture", null);
        using var anonymous = factory.CreateClient();
        using var denied = await anonymous.GetAsync(new Uri(Path(route.Route.Id, route.Version), UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        using var client = await factory.CreateLoggedInClientAsync("operator", Token);
        var before = await client.GetJsonAsync(Path(route.Route.Id, route.Version), Token);
        Assert.False(before.GetProperty("applied").GetBoolean());
        Assert.Empty(before.GetProperty("nodes").EnumerateArray());
        services.GetRequiredService<OutputControlStore>().Write(connection.Connection.Id, new(true, true), "fixture", null);
        var coordinator = services.GetRequiredService<RuntimeCoordinator>();
        Assert.True((await coordinator.ApplyDraftAsync(route.Version, "fixture", null, Token)).Activated);
        await OutputPipelineTests.UntilAsync(() => coordinator.Runtime.Messages.Read(
            TenonEdge.Abstractions.Plans.RuntimeUnitId.Create("output.http", "route/send"), includeBody: false)
            .Any(record => record.Details?.Outcome == TenonEdge.Runtime.Diagnostics.RuntimeMessageOutcome.Queued));
        using var response = await client.GetAsync(new Uri(Path(route.Route.Id, route.Version), UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        var page = await response.ReadJsonAsync(Token);
        Assert.True(page.GetProperty("applied").GetBoolean());
        Assert.Equal(1, page.GetProperty("revision").GetInt64());
        var nodes = page.GetProperty("nodes").EnumerateArray().ToDictionary(node => node.GetProperty("id").GetString()!);
        Assert.Equal(3, nodes.Count);
        Assert.Equal("received", nodes["input"].GetProperty("lastOutcome").GetString());
        Assert.Equal("processed", nodes["map"].GetProperty("lastOutcome").GetString());
        Assert.Equal("queued", nodes["send"].GetProperty("lastOutcome").GetString());
        Assert.Equal(1, nodes["send"].GetProperty("observedPaths").GetInt32());
        Assert.DoesNotContain("privateMessage", page.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("not-for-viewer", page.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("body", page.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("configuration", page.GetRawText(), StringComparison.OrdinalIgnoreCase);
        // A new draft node must not inherit old-node observations, even before the browser gates its view.
        var changedGraph = graph with
        {
            Nodes = graph.Nodes.Select(node => node.Id == "send" ? node with { Id = "new-send" } : node).ToArray(),
            Edges = [new("input", "map"), new("map", "new-send")]
        };
        var changed = drafts.Write(route.Route.Id, new(route.Version, "route", "New draft", true, changedGraph), "fixture", null);
        using var stale = await client.GetAsync(new Uri(Path(route.Route.Id, route.Version), UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var old = await client.GetJsonAsync(Path(route.Route.Id, changed.Version), Token);
        Assert.Contains(old.GetProperty("nodes").EnumerateArray(), node => node.GetProperty("id").GetString() == "send");
        Assert.DoesNotContain(old.GetProperty("nodes").EnumerateArray(), node => node.GetProperty("id").GetString() == "new-send");
        services.GetRequiredService<ConfigDatabase>().Client.Updateable<RuntimeSnapshotRecord>()
            .SetColumns(value => value.ContentHash == "corrupted").Where(value => value.Revision == 1).ExecuteCommand();
        using var corrupt = await client.GetAsync(new Uri(Path(route.Route.Id, changed.Version), UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.Conflict, corrupt.StatusCode);
    }

    [Fact]
    public async Task InvalidAndMissingRouteQueriesCannotAllocateRuntimeVersions()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "operator", ApiRequests.StrongPassword, UserRole.Viewer);
        using var client = await factory.CreateLoggedInClientAsync("operator", Token);
        foreach (var (id, version, status) in new[] { (0L, "0", HttpStatusCode.BadRequest), (1L, "", HttpStatusCode.BadRequest),
            (1L, new string('x', 65), HttpStatusCode.BadRequest), (1L, "0", HttpStatusCode.NotFound) })
        {
            using var response = await client.GetAsync(new Uri(Path(id, version), UriKind.Relative), Token);
            Assert.Equal(status, response.StatusCode);
        }
        Assert.Empty(factory.Services.GetRequiredService<RuntimeVersionStore>().ReadHistory());
    }
}
