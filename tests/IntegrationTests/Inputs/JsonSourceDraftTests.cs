using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Host.Features.Configuration;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Devices;
using TenonEdge.Host.Features.Inputs;
using TenonEdge.Host.Security;
using TenonEdge.IntegrationTests.Infrastructure;

namespace TenonEdge.IntegrationTests.Inputs;

public sealed class JsonSourceDraftTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("Mqtt")]
    [InlineData("Http")]
    [InlineData("Simulator")]
    public async Task TypedSourceApiPersistsAcrossHostRecreationAndRejectsDeviceFieldCollisions(string kind)
    {
        using var directory = TemporaryDirectory.Create();
        long deviceId;
        string version;
        await using (var factory = new FixedDataDirectoryApplicationFactory(directory.Path))
        {
            TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
            using var admin = await factory.CreateLoggedInClientAsync("admin", Token);
            var device = CreateDevice(factory, Enum.Parse<DeviceKind>(kind));
            deviceId = device.Device.Id;
            using var saved = await admin.PostJsonAsync($"/api/edge-admin/v1/devices/{deviceId}/inputs/", Request(device.Version, kind), Token);
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
            version = (await saved.ReadJsonAsync(Token)).GetProperty("version").GetString()!;
            using var collision = await admin.PostJsonAsync($"/api/edge-admin/v1/devices/{deviceId}/inputs/",
                Request(version, kind) with { Key = "slow" }, Token);
            Assert.Equal("input.duplicateDeviceField", await collision.ReadProblemCodeAsync(Token));
        }
        await using var restarted = new FixedDataDirectoryApplicationFactory(directory.Path);
        var stored = restarted.Services.GetRequiredService<InputDraftStore>().Read(deviceId);
        Assert.Equal(version, stored.Version);
        var input = Assert.Single(stored.Inputs);
        Assert.Equal(Enum.Parse<DeviceKind>(kind), input.Kind);
        Assert.Equal(["value"], InputDraftValidation.FieldKeys(input));
        if (kind == "Simulator")
        { Assert.Equal("""{"value":9007199254740993}""", input.Simulator!.SampleJson); }
    }

    [Fact]
    public async Task HttpConnectionChangeRevalidatesSourcesAndMalformedSampleIsBadRequest()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var device = CreateDevice(factory, DeviceKind.Http);
        var store = factory.Services.GetRequiredService<InputDraftStore>();
        var request = Request(device.Version, "Http");
        var source = store.Write(device.Device.Id, null, request with { Http = request.Http! with { Headers = new() { ["X-Request"] = new string('b', 3000) } } }, "fixture", null);
        var connections = factory.Services.GetRequiredService<ConnectionDraftStore>();
        // Existing source X-Request plus base headers must still obey the aggregate 16 KiB header limit.
        var large = Enumerable.Range(0, 4).ToDictionary(i => "X-" + i, _ => new string('a', 3500));
        Assert.Equal("device.sourceConflict", Assert.Throws<DraftWriteException>(() => connections.Write(device.Device.ConnectionId,
            new(source.Version, "connection", "HTTP", ConnectionKind.Http, Http: new("https://example.test/api/", Headers: large)), "fixture", null)).Code);
        Assert.Equal(source.Version, store.Read(device.Device.Id).Version);
        TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        using var admin = await factory.CreateLoggedInClientAsync("admin", Token);
        var simulated = factory.Services.GetRequiredService<DeviceDraftStore>().Write(null,
            new(source.Version, "sim", "Sim", DeviceKind.Simulator), "fixture", null);
        using var malformed = await admin.PostJsonAsync($"/api/edge-admin/v1/devices/{simulated.Device.Id}/inputs/",
            new InputDraftRequest(simulated.Version, "sim", "Sim", Simulator: new(SampleJson: "{broken")), Token);
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        Assert.Empty(store.Read(simulated.Device.Id).Inputs);
    }

    [Theory]
    [InlineData("""{"key":"v","path":"/value"}""", "null")]
    [InlineData("""{"key":"v","path":"/value","type":"number"}""", """{"path":"/stamp"}""")]
    public async Task FieldTypesAndTimestampFormatsMustBeExplicit(string field, string time)
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var device = CreateDevice(factory, DeviceKind.Mqtt);
        TestAccounts.Create(factory.Services, "admin", ApiRequests.StrongPassword, UserRole.Administrator);
        using var admin = await factory.CreateLoggedInClientAsync("admin", Token);
        using var body = new StringContent($$$"""{"expectedVersion":"{{{device.Version}}}","key":"input","name":"Input","mqtt":{"topicFilter":"plant/meter","fields":[{{{field}}}],"upstreamTime":{{{time}}}}}""", Encoding.UTF8, "application/json");
        using var result = await admin.PostAsync($"/api/edge-admin/v1/devices/{device.Device.Id}/inputs/", body, Token);
        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        Assert.True((await result.ReadJsonAsync(Token)).GetProperty("errors").TryGetProperty("input", out _));
        Assert.Empty(factory.Services.GetRequiredService<InputDraftStore>().Read(device.Device.Id).Inputs);
    }

    private static InputDraftRequest Request(string version, string kind) => new(version, "fast", "Fast",
        Mqtt: kind == "Mqtt" ? new("plant/meter", [new("value", "/value", FieldType.Number)]) : null,
        Http: kind == "Http" ? new("values?site=plant", [new("value", "/value", FieldType.Number)], Headers: new() { ["X-Request"] = "one" }) : null,
        Simulator: kind == "Simulator" ? new(SampleJson: """{"value":9007199254740993}""") : null);

    private static DeviceDraftResult CreateDevice(TenonEdgeApplicationFactory factory, DeviceKind kind)
    {
        var version = "0";
        long? connectionId = null;
        if (kind != DeviceKind.Simulator)
        {
            var connection = factory.Services.GetRequiredService<ConnectionDraftStore>().Write(null,
                new(version, "connection", "Connection", kind == DeviceKind.Mqtt ? ConnectionKind.Mqtt : ConnectionKind.Http,
                    Mqtt: kind == DeviceKind.Mqtt ? new("10.0.0.1") : null,
                    Http: kind == DeviceKind.Http ? new("https://example.test/api/") : null), "fixture", null);
            connectionId = connection.Connection.Id;
            version = connection.Version;
        }
        return factory.Services.GetRequiredService<DeviceDraftStore>().Write(null,
            new(version, "meter", "Meter", kind, ConnectionId: connectionId), "fixture", null);
    }
}
