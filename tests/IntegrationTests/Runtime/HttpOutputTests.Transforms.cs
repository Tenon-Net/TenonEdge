using System.Text.Json;
using TenonEdge.Abstractions.Plans;
using TenonEdge.Adapters.Http;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.Runtime.Connections;

namespace TenonEdge.IntegrationTests.Runtime;

public sealed partial class HttpOutputTests
{
    private static readonly string[] TransformInputs = ["meter/sim"];

    [Fact]
    public async Task RegisteredTransformReachesRealHttpAndRestartResendsOriginalFrozenBytesWithoutTransformingAgain()
    {
        using var directory = TemporaryDirectory.Create();
        using var server = Listen();
        var events = new RecordingEventSink();
        RuntimeUnitPlan Output(int offset) => new(RuntimeUnitId.Create("output.http", "transformed"), JsonSerializer.SerializeToElement(new
        {
            connectionId = 1,
            baseAddress = Base(server),
            path = "transformed",
            inputs = TransformInputs,
            transformStages = new[] { TransformRuntimeTests.Stage(offset) }
        }));
        HttpOutputFactory Factory() => new(new Credentials(), TimeProvider.System, new OutboundConnector(new LoopbackPolicy()), new SharedConnections());
        var extension = new TestOffsetTransformFactory();
        (string Header, byte[] Body) firstWire;
        Guid deliveryId;
        await using (var first = RuntimeTesting.CreateRuntime([new OneShotInputFactory()], directory.Path, events,
            outputs: [Factory()], transforms: new([extension])))
        {
            var request = ServeOnceAsync(server, 401); // Pause, retaining the committed bytes without a retry race.
            await using var plan = first.Prepare(OutputPipelineTests.Plan(OutputPipelineTests.Input(), Output(1)));
            Assert.Empty(plan.Failures);
            first.StartCore();
            await first.ActivateAsync(plan);
            firstWire = await request.WaitAsync(TimeSpan.FromSeconds(10), Token);
            await OutputPipelineTests.UntilAsync(() => events.OfType("output.suspended").Count == 1);
            await first.StopAsync(Token);
            var pending = Assert.Single(first.Deliveries.ReadPending(1, 0, 2));
            deliveryId = pending.DeliveryId;
            Assert.Equal(pending.Payload.ToArray(), firstWire.Body);
            Assert.Equal(1, extension.Calls);
        }
        var nextExtension = new TestOffsetTransformFactory();
        await using var second = RuntimeTesting.CreateRuntime([new OneShotInputFactory(0)], directory.Path,
            outputs: [Factory()], transforms: new([nextExtension]));
        await using var next = second.Prepare(new(2, [OutputPipelineTests.Input(), Output(13)]));
        Assert.Empty(next.Failures);
        var resumed = ServeOnceAsync(server, 204);
        second.StartCore();
        await second.ActivateAsync(next);
        var secondWire = await resumed.WaitAsync(TimeSpan.FromSeconds(10), Token);
        await OutputPipelineTests.UntilAsync(() => second.Deliveries.GetStatus().DeletedRecords == 1);
        await second.StopAsync(Token);
        Assert.Equal(firstWire.Body, secondWire.Body);
        Assert.Contains($"Idempotency-Key: {deliveryId:D}", secondWire.Header, StringComparison.OrdinalIgnoreCase);
        using var json = JsonDocument.Parse(secondWire.Body);
        Assert.Equal(43, json.RootElement.GetProperty("values").GetProperty("value").GetInt64());
        Assert.Equal(0, nextExtension.Calls);
        Assert.Equal(0, second.InFlight.Status.ReservedBytes);
    }
}
