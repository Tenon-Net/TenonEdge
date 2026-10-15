using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MQTTnet;
using MQTTnet.Protocol;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Credentials;
using TenonEdge.Host.Features.Devices;
using TenonEdge.Host.Features.Inputs;
using TenonEdge.Host.Features.Routes;
using TenonEdge.Host.Security;
using TenonEdge.Host.Storage;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.IntegrationTests.Probes.Mqtt;
using TenonEdge.Runtime.Lifecycle;

namespace TenonEdge.IntegrationTests.Routes;

[Trait("Category", "RealBroker")]
public sealed class RouteMqttPreviewTests(MosquittoBrokerFixture fixture) : IClassFixture<MosquittoBrokerFixture>
{
    private const string Endpoint = "/api/edge-admin/v1/routes/preview/mqtt";
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("Administrator", HttpStatusCode.OK)]
    [InlineData("Configurator", HttpStatusCode.OK)]
    [InlineData("Viewer", HttpStatusCode.Forbidden)]
    public async Task RetainedSampleUsesRealBrokerAndExactValuesWithoutOutputOrDraftChanges(string role, HttpStatusCode expected)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var broker = fixture.Require();
        var topic = MqttProbe.NewTopic("route-sample");
        await Publish(topic, """{"value":9223372036854775807}""");
        await using var factory = new TenonEdgeApplicationFactory();
        var request = await Seed(factory.Services, broker.Port, topic, ((IPEndPoint)listener.LocalEndpoint).Port);
        TestAccounts.Create(factory.Services, "operator", ApiRequests.StrongPassword, Enum.Parse<UserRole>(role));
        using var client = await factory.CreateLoggedInClientAsync("operator", Token);
        using var response = await client.PostJsonAsync(Endpoint, request, Token);
        Assert.Equal(expected, response.StatusCode);
        if (expected == HttpStatusCode.OK)
        {
            Assert.True((await response.Content.ReadAsByteArrayAsync(Token)).Length <= 65536);
            var json = await response.ReadJsonAsync(Token);
            Assert.True(json.GetProperty("previewed").GetBoolean());
            Assert.True(json.GetProperty("mqtt").GetProperty("retained").GetBoolean());
            Assert.False(json.GetProperty("mqtt").GetProperty("duplicate").GetBoolean());
            Assert.Equal(1, json.GetProperty("mqtt").GetProperty("qos").GetInt32());
            var result = json.GetProperty("result");
            Assert.Equal(1, result.GetProperty("succeededPaths").GetInt32());
            Assert.Equal("""{"result":9223372036854775807}""", result.GetProperty("paths")[0].GetProperty("output").GetProperty("body").GetString());
        }
        Assert.False(listener.Pending());
        Assert.Equal(request.ExpectedVersion, factory.Services.GetRequiredService<ConnectionDraftStore>().Read().Version);
        Assert.Empty(factory.Services.GetRequiredService<RouteDraftStore>().Read().Routes);
        Assert.Equal(0, factory.Services.GetRequiredService<RuntimeHost>().InFlight.Status.ReservedBytes);
    }

    [Theory]
    [InlineData(40000)]
    [InlineData(700000)]
    public async Task LargeCompleteSampleCanMapRawTailWithoutDisplayReparse(int size)
    {
        var broker = fixture.Require();
        var topic = MqttProbe.NewTopic("route-full");
        await Publish(topic, "{\"padding\":\"" + new string('x', size) + "\",\"value\":1,\"tail\":9223372036854775807}");
        await using var factory = new TenonEdgeApplicationFactory();
        var request = await Seed(factory.Services, broker.Port, topic, 1);
        request = request with
        {
            Graph = request.Graph with
            {
                Nodes = [.. request.Graph.Nodes,
                    new("map", "transform.map", 1, "Map", JsonSerializer.Deserialize<JsonElement>(
                        """{"rules":[{"target":"value","sourcePath":"/tail","type":"integer","required":true}]}"""))],
                Edges = [new("in", "map"), new("map", "out")]
            }
        };
        TestAccounts.Create(factory.Services, "operator", ApiRequests.StrongPassword, UserRole.Configurator);
        using var client = await factory.CreateLoggedInClientAsync("operator", Token);
        using var response = await client.PostJsonAsync(Endpoint, request, Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.ReadJsonAsync(Token);
        Assert.Equal("{\"result\":9223372036854775807}", json.GetProperty("result").GetProperty("paths")[0].GetProperty("output").GetProperty("body").GetString());
        Assert.True(json.GetProperty("mqtt").GetProperty("retained").GetBoolean());
        Assert.Equal(0, factory.Services.GetRequiredService<RuntimeHost>().InFlight.Status.ReservedBytes);
    }

    [Theory]
    [InlineData("redacted", "preview.mqtt.payload.redacted")]
    [InlineData("large", "preview.mqtt.payload.tooLarge")]
    [InlineData("invalid", "preview.mqtt.payload.invalidJson")]
    public async Task UnusableCaptureCannotBecomeARouteSample(string kind, string code)
    {
        var broker = fixture.Require();
        var topic = MqttProbe.NewTopic("route-reject");
        var payload = kind switch
        {
            "redacted" => """{"password":"fixture-secret","value":42}""",
            "large" => JsonSerializer.Serialize(new { value = 42, padding = new string('x', 1024 * 1024) }),
            _ => "not-json"
        };
        await Publish(topic, payload);
        await using var factory = new TenonEdgeApplicationFactory();
        var request = await Seed(factory.Services, broker.Port, topic, 1);
        TestAccounts.Create(factory.Services, "operator", ApiRequests.StrongPassword, UserRole.Configurator);
        using var client = await factory.CreateLoggedInClientAsync("operator", Token);
        using var response = await client.PostJsonAsync(Endpoint, request, Token);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync(Token);
        Assert.Contains(code, text, StringComparison.Ordinal);
        Assert.DoesNotContain("fixture-secret", text, StringComparison.Ordinal);
        Assert.DoesNotContain("succeededPaths", text, StringComparison.Ordinal);
        Assert.Equal(0, factory.Services.GetRequiredService<RuntimeHost>().InFlight.Status.ReservedBytes);
    }

    [Fact]
    public async Task RevokedCredentialNeverDowngradesToAnonymous()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var request = await Seed(factory.Services, fixture.Require().Port, MqttProbe.NewTopic("revoked"), 1);
        using (var database = factory.Services.GetRequiredService<ConfigDatabase>().CreateIsolatedClient())
        { database.Updateable<MqttCredentialRecord>().SetColumns(row => row.ProtectedPassword == null).Where(row => row.ConnectionId > 0).ExecuteCommand(); }
        TestAccounts.Create(factory.Services, "operator", ApiRequests.StrongPassword, UserRole.Configurator);
        using var client = await factory.CreateLoggedInClientAsync("operator", Token);
        using var response = await client.PostJsonAsync(Endpoint, request, Token);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("preview.mqtt.credential.unavailable", await response.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnonymousCsrfStaleVersionAndOutsideSourceCannotCapture()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var request = await Seed(factory.Services, fixture.Require().Port, MqttProbe.NewTopic("denied"), 1);
        using var anonymous = factory.CreateClient();
        using var denied = await anonymous.PostJsonAsync(Endpoint, request, Token);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        TestAccounts.Create(factory.Services, "operator", ApiRequests.StrongPassword, UserRole.Configurator);
        using var client = await factory.CreateLoggedInClientAsync("operator", Token);
        using var stale = await client.PostJsonAsync(Endpoint, request with { ExpectedVersion = "stale" }, Token);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        using var foreign = await client.PostJsonAsync(Endpoint, request with { SourceId = request.SourceId + 1 }, Token);
        Assert.Equal(HttpStatusCode.BadRequest, foreign.StatusCode);
        client.DefaultRequestHeaders.Remove(ApiRequests.CsrfHeader);
        using var csrf = await client.PostJsonAsync(Endpoint, request, Token);
        Assert.Equal(HttpStatusCode.Forbidden, csrf.StatusCode);
        Assert.Equal(0, factory.Services.GetRequiredService<RuntimeHost>().InFlight.Status.ReservedBytes);
    }

    private async Task Publish(string topic, string body)
    {
        var (publisher, _) = await MqttProbe.ConnectAsync(MqttProbe.Options(fixture.Require(), "publisher-" + Guid.NewGuid().ToString("N")), Token);
        using (publisher)
        {
            await publisher.PublishAsync(new MqttApplicationMessageBuilder().WithTopic(topic).WithPayload(body)
                .WithRetainFlag().WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).Build(), Token);
        }
    }

    private static async Task<RouteMqttPreviewRequest> Seed(IServiceProvider services, int port, string topic, int outputPort)
    {
        services.GetRequiredService<OutboundPolicyStore>().Write(new([], [port, outputPort]), "fixture", null);
        var connections = services.GetRequiredService<ConnectionDraftStore>();
        var source = connections.Write(null, new(connections.Read().Version, "mqtt", "MQTT", ConnectionKind.Mqtt,
            Mqtt: new("127.0.0.1", port, ClientId: "route-production-id")), "fixture", null);
        var output = connections.Write(null, new(source.Version, "output", "Output", ConnectionKind.Http,
            Http: new($"http://127.0.0.1:{outputPort}/")), "fixture", null);
        var device = services.GetRequiredService<DeviceDraftStore>().Write(null,
            new(output.Version, "device", "Device", DeviceKind.Mqtt, ConnectionId: source.Connection.Id), "fixture", null);
        var input = services.GetRequiredService<InputDraftStore>().Write(device.Device.Id, null,
            new(device.Version, "values", "Values", Mqtt: new(topic, [new("value", "/value", FieldType.Integer)], Qos: 1)), "fixture", null);
        await services.GetRequiredService<MqttCredentialStore>().WriteAsync(source.Connection.Id,
            new($"mqtt://127.0.0.1:{port}", MosquittoBroker.UserName, MosquittoBroker.Password, ExpectedDraftVersion: input.Version), "fixture", null, false, token: TestContext.Current.CancellationToken);
        var graph = new RouteGraph(1,
            [new("in", "input.mqtt", 1, "Input", JsonSerializer.SerializeToElement(new { sourceIds = new[] { input.Input.Id } })),
            new("out", "output.http", 1, "Output", JsonSerializer.SerializeToElement(new { connectionId = output.Connection.Id,
                payload = new { mode = "custom", fields = new[] { new { target = "result", sourceField = "value" } } } }))],
            [new("in", "out")], new([]));
        return new(input.Version, graph, input.Input.Id);
    }
}
