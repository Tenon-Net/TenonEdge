using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Host.Features.Devices;
using TenonEdge.Host.Features.Inputs;
using TenonEdge.Host.Features.Routes;
using TenonEdge.Host.Security;
using TenonEdge.Host.Storage;
using TenonEdge.IntegrationTests.Infrastructure;

namespace TenonEdge.IntegrationTests.Routes;

public sealed class RouteFieldTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    [Theory]
    [InlineData("Viewer", HttpStatusCode.Forbidden)]
    [InlineData("Configurator", HttpStatusCode.OK)]
    [InlineData("Administrator", HttpStatusCode.OK)]
    public async Task UnsavedGraphFieldAnalysisIsVersionedAuthorizedAndHasNoSaveOrApplyEffects(string role, HttpStatusCode expected)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var device = factory.Services.GetRequiredService<DeviceDraftStore>().Write(null, new("0", "sim", "Sim", DeviceKind.Simulator), "test", null);
        var source = factory.Services.GetRequiredService<InputDraftStore>().Write(device.Device.Id, null,
            new(device.Version, "values", "Values", Simulator: new(SampleJson: "{\"privateSample\":9223372036854775807}")), "test", null);
        var graph = new RouteGraph(1, [new("in", "input.simulator", 1, "Input", JsonSerializer.SerializeToElement(new { sourceIds = new[] { source.Input.Id } })),
            new("map", "transform.map", 1, "Incomplete", JsonSerializer.SerializeToElement(new { rules = Array.Empty<object>() }))], [new("in", "map")], new([]));
        TestAccounts.Create(factory.Services, "operator", ApiRequests.StrongPassword, Enum.Parse<UserRole>(role));
        using var client = await factory.CreateLoggedInClientAsync("operator", Token);
        using var response = await client.PostJsonAsync("/api/edge-admin/v1/routes/fields", new RouteFieldRequest(source.Version, graph, "map"), Token);
        Assert.Equal(expected, response.StatusCode);
        if (expected == HttpStatusCode.OK)
        {
            var raw = await response.Content.ReadAsStringAsync(Token);
            Assert.DoesNotContain("9223372036854775807", raw, StringComparison.Ordinal);
            var result = await response.ReadJsonAsync(Token);
            Assert.True(result.GetProperty("analyzed").GetBoolean());
            Assert.Equal("privateSample", Assert.Single(result.GetProperty("fields").EnumerateArray()).GetProperty("key").GetString());
            using var stale = await client.PostJsonAsync("/api/edge-admin/v1/routes/fields", new RouteFieldRequest("0", graph, "map"), Token);
            Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        }
        var drafts = factory.Services.GetRequiredService<RouteDraftStore>().Read();
        Assert.Empty(drafts.Routes);
        Assert.Equal(source.Version, drafts.Version);
        Assert.Null(factory.Services.GetRequiredService<ConfigDatabase>().Client.Queryable<SystemStateRecord>().InSingle(1).EffectiveRevision);
    }
}
