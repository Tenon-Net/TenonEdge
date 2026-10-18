using System.Buffers;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Plans;
using TenonEdge.Adapters.Mqtt;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Credentials;
using TenonEdge.Host.Features.Routes;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.IntegrationTests.Probes.Mqtt;
using TenonEdge.IntegrationTests.Routes;
using TenonEdge.Runtime.Connections;
using TenonEdge.Runtime.Deliveries;

namespace TenonEdge.IntegrationTests.Runtime;

public sealed partial class MqttOutputTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task RegisteredTransformUsesOriginalMqttSenderAndConfirmation(int qos)
    {
        var broker = fixture.Require();
        var topic = MqttProbe.NewTopic("registered-transform");
        var (subscriber, _) = await MqttProbe.ConnectAsync(MqttProbe.Options(broker, NewClientId()), Token);
        using (subscriber)
        using (var directory = TemporaryDirectory.Create())
        {
            var received = ReceivedMessages.Attach(subscriber);
            await MqttProbe.SubscribeAsync(subscriber, topic, Token);
            var settings = MqttOutputSettings.Parse(Plan(broker.Port, topic, qos).Configuration) with
            { TransformStages = JsonSerializer.SerializeToElement(new[] { TransformRuntimeTests.Stage(1) }) };
            var output = new RuntimeUnitPlan(RuntimeUnitId.Create("output.mqtt", "transformed"), JsonSerializer.SerializeToElement(settings, JsonSerializerOptions.Web));
            var extension = new TestOffsetTransformFactory();
            await using var runtime = RuntimeTesting.CreateRuntime([new OneShotInputFactory()], directory.Path,
                outputs: [new MqttOutputFactory(new Credentials(), TimeProvider.System, LoopbackConnector, new SharedConnections())], transforms: new([extension]));
            await using var plan = runtime.Prepare(OutputPipelineTests.Plan(OutputPipelineTests.Input(), output));
            Assert.Empty(plan.Failures);
            runtime.StartCore();
            await runtime.ActivateAsync(plan);
            var wire = await received.NextAsync(Token);
            using var json = JsonDocument.Parse(wire.ApplicationMessage.Payload.ToArray());
            Assert.Equal(43, json.RootElement.GetProperty("values").GetProperty("value").GetInt64());
            Assert.Equal(topic, wire.ApplicationMessage.Topic);
            await OutputPipelineTests.UntilAsync(() => runtime.Deliveries.GetStatus().DeletedRecords == 1);
            await runtime.StopAsync(Token);
            Assert.Equal(1, extension.Calls);
            Assert.Empty(runtime.Deliveries.ReadPending(1, 0, 2));
            Assert.Equal(0, runtime.InFlight.Status.ReservedBytes);
        }
    }
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task SavedMixedTransformGraphAppliesThroughOriginalHostAndPublishesMqtt(int qos)
    {
        var broker = fixture.Require();
        var topic = MqttProbe.NewTopic("graph-transform");
        var (subscriber, _) = await MqttProbe.ConnectAsync(MqttProbe.Options(broker, NewClientId()), Token);
        using (subscriber)
        using (var data = TemporaryDirectory.Create())
        {
            var received = ReceivedMessages.Attach(subscriber);
            await MqttProbe.SubscribeAsync(subscriber, topic, Token);
            await using var host = new FixedDataDirectoryApplicationFactory(data.Path, new Dictionary<string, string>
            { ["TenonEdge:Outbound:LocalPorts:0"] = broker.Port.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                services => services.AddSingleton<IMessageTransformFactory>(new TestOffsetTransformFactory()));
            var seed = RoutePreviewTests.Seed(host.Services, 18191);
            var connection = host.Services.GetRequiredService<ConnectionDraftStore>().Write(null,
                new(seed.ExpectedVersion, "mqtt", "MQTT", ConnectionKind.Mqtt, Mqtt: new("127.0.0.1", broker.Port)), "test", null);
            await host.Services.GetRequiredService<MqttCredentialStore>().WriteAsync(connection.Connection.Id,
                new($"mqtt://127.0.0.1:{broker.Port}", MosquittoBroker.UserName, MosquittoBroker.Password, ExpectedDraftVersion: connection.Version),
                "test", null, false, token: Token);
            var graph = RouteTransformTests.Graph(seed.Graph, "{\"offset\":2}");
            graph.Nodes[^1] = new("out", "output.mqtt", 1, "MQTT", JsonSerializer.SerializeToElement(new { connectionId = connection.Connection.Id, topic, qos }));
            var saved = host.Services.GetRequiredService<RouteDraftStore>().Write(null,
                new(connection.Version, "mixed", "Mixed", true, graph), "test", null);
            var applied = await host.Services.GetRequiredService<RuntimeCoordinator>().ApplyDraftAsync(saved.Version, "test", null, Token);
            Assert.True(applied.Activated);
            var wire = await received.NextAsync(Token);
            using var json = JsonDocument.Parse(wire.ApplicationMessage.Payload.ToArray());
            Assert.Equal(3, json.RootElement.GetProperty("values").GetProperty("final").GetInt64());
            Assert.Equal(topic, wire.ApplicationMessage.Topic);
            await OutputPipelineTests.UntilAsync(() => host.Services.GetRequiredService<DeliveryCache>().GetStatus().DeletedRecords >= 1);
        }
    }

}
