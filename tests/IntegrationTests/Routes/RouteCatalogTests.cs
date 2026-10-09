using System.Net;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Devices;
using TenonEdge.Host.Features.Inputs;
using TenonEdge.Host.Features.Routes;
using TenonEdge.Host.Security;
using TenonEdge.Host.Storage;
using TenonEdge.IntegrationTests.Infrastructure;

namespace TenonEdge.IntegrationTests.Routes;

public sealed class RouteCatalogTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("Viewer")]
    [InlineData("Configurator")]
    [InlineData("Administrator")]
    public async Task CatalogIsAuthenticatedMetadataOnlyAndAllRolesCanRead(string role)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var devices = factory.Services.GetRequiredService<DeviceDraftStore>();
        var device = devices.Write(null, new("0", "sim", "Simulator", DeviceKind.Simulator), "test", null);
        var source = factory.Services.GetRequiredService<InputDraftStore>().Write(device.Device.Id, null,
            new(device.Version, "sample", "Sample", Simulator: new(SampleJson: "{\"privateSampleMarker\":42}")), "test", null);
        var store = factory.Services.GetRequiredService<RouteDraftStore>();
        var before = store.Read().Version;
        using var anonymous = factory.CreateClient();
        using var denied = await anonymous.GetAsync(new Uri("/api/edge-admin/v1/routes/catalog?kind=input.simulator", UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        TestAccounts.Create(factory.Services, "operator", ApiRequests.StrongPassword, Enum.Parse<UserRole>(role));
        using var client = await factory.CreateLoggedInClientAsync("operator", Token);
        using var response = await client.GetAsync(new Uri("/api/edge-admin/v1/routes/catalog?kind=input.simulator", UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync(Token);
        Assert.DoesNotContain("privateSampleMarker", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("sampleJson", raw, StringComparison.Ordinal);
        var json = await response.ReadJsonAsync(Token);
        Assert.Equal(before, json.GetProperty("version").GetString());
        var item = Assert.Single(json.GetProperty("items").EnumerateArray());
        Assert.Equal(source.Input.Id, item.GetProperty("id").GetInt64());
        Assert.Equal(device.Device.Id, item.GetProperty("deviceId").GetInt64());
        Assert.Equal("sim", item.GetProperty("deviceKey").GetString());
        Assert.True(item.GetProperty("enabled").GetBoolean());
        Assert.Equal(before, store.Read().Version);
    }

    [Fact]
    public async Task CatalogPaginatesByIdFiltersProtocolAndReportsChangedVersionWithoutReadingConfigBodies()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var connections = factory.Services.GetRequiredService<ConnectionDraftStore>();
        var first = connections.Write(null, new("0", "a", "A", ConnectionKind.Http, Http: new("http://localhost/")), "test", null);
        var second = connections.Write(null, new(first.Version, "b", "B", ConnectionKind.Http, Enabled: false, Http: new("http://localhost/")), "test", null);
        var mqtt = connections.Write(null, new(second.Version, "m", "M", ConnectionKind.Mqtt, Mqtt: new("localhost")), "test", null);
        var store = factory.Services.GetRequiredService<RouteDraftStore>();
        var page = store.ReadCatalog("output.http", 0, 1);
        Assert.Equal(first.Connection.Id, Assert.Single(page.Items).Id);
        Assert.Equal(first.Connection.Id, page.NextAfterId);
        var next = store.ReadCatalog("output.http", page.NextAfterId.GetValueOrDefault(), 1);
        Assert.Equal(page.Version, next.Version);
        Assert.False(Assert.Single(next.Items).Enabled);
        Assert.Null(next.NextAfterId);
        Assert.Equal(mqtt.Connection.Id, Assert.Single(store.ReadCatalog("output.mqtt", 0, 100).Items).Id);
        var device = factory.Services.GetRequiredService<DeviceDraftStore>().Write(null, new(mqtt.Version, "sim", "Sim", DeviceKind.Simulator, Enabled: false), "test", null);
        var input = factory.Services.GetRequiredService<InputDraftStore>().Write(device.Device.Id, null,
            new(device.Version, "data", "Data", Simulator: new(Template: "meter")), "test", null);
        // A large unrelated field is valid JSON but invalid typed config: catalog must not decode it.
        factory.Services.GetRequiredService<ConfigDatabase>().Client.Ado.ExecuteCommand(
            "UPDATE input_draft SET content = json_set(content, '$.unrelated', printf('%.*c', 2000000, 'x'))");
        var sources = store.ReadCatalog("input.simulator", 0, 100);
        Assert.Equal(input.Version, sources.Version);
        Assert.NotEqual(page.Version, sources.Version);
        Assert.False(Assert.Single(sources.Items).Enabled);
        Assert.Empty(store.ReadCatalog("input.modbus", 0, 100).Items);
    }

    [Theory]
    [InlineData("script", 0, 100)]
    [InlineData("output.tenon", 0, 100)]
    [InlineData("input.simulator", -1, 100)]
    [InlineData("input.simulator", 0, 0)]
    [InlineData("input.simulator", 0, 101)]
    public async Task InvalidPagingOrKindsReturnValidationProblems(string kind, long after, int limit)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "reader", ApiRequests.StrongPassword, UserRole.Viewer);
        using var client = await factory.CreateLoggedInClientAsync("reader", Token);
        using var response = await client.GetAsync(new Uri($"/api/edge-admin/v1/routes/catalog?kind={kind}&afterId={after}&limit={limit}", UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
