using System.Buffers;
using System.Text.Json;
using TenonEdge.Abstractions.Plans;
using TenonEdge.Adapters.Mqtt;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.IntegrationTests.Probes.Mqtt;
using TenonEdge.Runtime.Connections;

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
}
