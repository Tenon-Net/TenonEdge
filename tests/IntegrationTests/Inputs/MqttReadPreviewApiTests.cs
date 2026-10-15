using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using MQTTnet;
using MQTTnet.Protocol;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Adapters.Mqtt;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Credentials;
using TenonEdge.Host.Features.Devices;
using TenonEdge.Host.Features.Inputs;
using TenonEdge.Host.Security;
using TenonEdge.Host.Storage;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.IntegrationTests.Probes.Mqtt;
using TenonEdge.Runtime.Diagnostics;
using TenonEdge.Runtime.Lifecycle;

namespace TenonEdge.IntegrationTests.Inputs;

[Trait("Category", "RealBroker")]
public sealed class MqttReadPreviewApiTests(MosquittoBrokerFixture fixture) : IClassFixture<MosquittoBrokerFixture>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static string Endpoint(long device) => $"/api/edge-admin/v1/devices/{device}/preview/mqtt";
    private static MqttSourceDraft Source(string topic) => new(topic, [new("value", "/value", FieldType.Integer)]);

    [Theory]
    [InlineData("Administrator", HttpStatusCode.OK)]
    [InlineData("Configurator", HttpStatusCode.OK)]
    [InlineData("Viewer", HttpStatusCode.Forbidden)]
    public async Task RolesGuardRetainedCaptureAndUnsavedTopicDoesNotChangeDraft(string role, HttpStatusCode expected)
    {
        var broker = fixture.Require();
        var topic = MqttProbe.NewTopic("api-sample");
        var (publisher, _) = await MqttProbe.ConnectAsync(MqttProbe.Options(broker, "publish-" + Guid.NewGuid().ToString("N")), Token);
        using (publisher)
        {
            await publisher.PublishAsync(new MqttApplicationMessageBuilder().WithTopic(topic).WithPayload("{\"value\":9223372036854775807}")
                .WithRetainFlag().WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).Build(), Token);
        }
        await using var factory = new TenonEdgeApplicationFactory();
        var (device, input, version) = await Setup(factory.Services, broker.Port);
        TestAccounts.Create(factory.Services, "tester", ApiRequests.StrongPassword, Enum.Parse<UserRole>(role));
        using var client = await factory.CreateLoggedInClientAsync("tester", Token);
        using var response = await client.PostJsonAsync(Endpoint(device), new MqttReadPreviewRequest(version, input, Source(topic)), Token);
        Assert.Equal(expected, response.StatusCode);
        if (expected == HttpStatusCode.OK)
        {
            var result = (await response.Content.ReadFromJsonAsync<MqttReadPreviewResult>(Token))!;
            Assert.Equal("success", result.Code);
            Assert.True(result.Retained);
            Assert.Equal(topic, result.Topic);
            Assert.Equal("9223372036854775807", Assert.Single(result.Preview!.Fields).Text);
        }
        var draft = factory.Services.GetRequiredService<InputDraftStore>().Read(device);
        Assert.Equal(version, draft.Version);
        Assert.Equal("saved/topic", Assert.Single(draft.Inputs).Mqtt!.TopicFilter);
        Assert.Equal(0, factory.Services.GetRequiredService<RuntimeHost>().InFlight.Status.ReservedBytes);
    }

    [Fact]
    public async Task AnonymousCsrfVersionOwnershipAndInvalidFiltersRejectBeforeCapture()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var (device, input, version) = await Setup(factory.Services, fixture.Require().Port);
        using var anonymous = factory.CreateClient();
        using var unauthenticated = await anonymous.PostJsonAsync(Endpoint(device), new MqttReadPreviewRequest(version, input), Token);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        TestAccounts.Create(factory.Services, "tester", ApiRequests.StrongPassword, UserRole.Configurator);
        using var client = await factory.CreateLoggedInClientAsync("tester", Token);
        using var stale = await client.PostJsonAsync(Endpoint(device), new MqttReadPreviewRequest("stale", input), Token);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        using var foreign = await client.PostJsonAsync(Endpoint(device), new MqttReadPreviewRequest(version, input + 1, Source("topic")), Token);
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        using var invalid = await client.PostJsonAsync(Endpoint(device), new MqttReadPreviewRequest(version, input, Source("bad/#/tail")), Token);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using var missing = await client.PostJsonAsync(Endpoint(device), new MqttReadPreviewRequest(version), Token);
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        client.DefaultRequestHeaders.Remove(ApiRequests.CsrfHeader);
        using var csrf = await client.PostJsonAsync(Endpoint(device), new MqttReadPreviewRequest(version, input), Token);
        Assert.Equal(HttpStatusCode.Forbidden, csrf.StatusCode);
    }

    [Fact]
    public async Task RevokedBoundCredentialDoesNotFallBackToAnonymous()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        var (device, input, version) = await Setup(factory.Services, fixture.Require().Port);
        using (var database = factory.Services.GetRequiredService<ConfigDatabase>().CreateIsolatedClient())
        { database.Updateable<MqttCredentialRecord>().SetColumns(record => record.ProtectedPassword == null).Where(record => record.ConnectionId > 0).ExecuteCommand(); }
        TestAccounts.Create(factory.Services, "tester", ApiRequests.StrongPassword, UserRole.Configurator);
        using var client = await factory.CreateLoggedInClientAsync("tester", Token);
        using var response = await client.PostJsonAsync(Endpoint(device), new MqttReadPreviewRequest(version, input), Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("credential.unavailable", (await response.Content.ReadFromJsonAsync<MqttReadPreviewResult>(Token))!.Code);
    }

    [Fact]
    public async Task SharesSessionQuotaWithOtherProtocolPreviews()
    {
        await using var factory = new TenonEdgeApplicationFactory();
        TestAccounts.Create(factory.Services, "tester", ApiRequests.StrongPassword, UserRole.Configurator);
        using var client = await factory.CreateLoggedInClientAsync("tester", Token);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(client.DefaultRequestHeaders.GetValues(ApiRequests.CsrfHeader).Single())));
        var budget = factory.Services.GetRequiredService<PreviewOperationBudget>();
        using var first = budget.TryAcquire(key);
        using var second = budget.TryAcquire(key);
        using var response = await client.PostJsonAsync(Endpoint(1), new MqttReadPreviewRequest("0", 1), Token);
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
    }

    private static async Task<(long Device, long Input, string Version)> Setup(IServiceProvider services, int port)
    {
        services.GetRequiredService<OutboundPolicyStore>().Write(new([], [port]), "fixture", null);
        var connections = services.GetRequiredService<ConnectionDraftStore>();
        var connection = connections.Write(null, new(connections.Read().Version, "mqtt", "MQTT", ConnectionKind.Mqtt,
            Mqtt: new("127.0.0.1", port)), "fixture", null);
        var device = services.GetRequiredService<DeviceDraftStore>().Write(null,
            new(connection.Version, "device", "Device", DeviceKind.Mqtt, ConnectionId: connection.Connection.Id), "fixture", null);
        var input = services.GetRequiredService<InputDraftStore>().Write(device.Device.Id, null,
            new(device.Version, "values", "Values", Mqtt: Source("saved/topic")), "fixture", null);
        await services.GetRequiredService<MqttCredentialStore>().WriteAsync(connection.Connection.Id,
            new($"mqtt://127.0.0.1:{port}", MosquittoBroker.UserName, MosquittoBroker.Password, ExpectedDraftVersion: input.Version), "fixture", null, false, token: TestContext.Current.CancellationToken);
        return (device.Device.Id, input.Input.Id, input.Version);
    }
}
