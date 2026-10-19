using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Deliveries;
using TenonEdge.Host.Features.Devices;
using TenonEdge.Host.Features.Inputs;
using TenonEdge.Host.Features.Routes;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Storage;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.Runtime.Deliveries;
using TenonEdge.Runtime.Lifecycle;

namespace TenonEdge.IntegrationTests.Runtime;

public sealed partial class ExtensionOutputRuntimeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueueTargetCannotReplaceItsFrozenConnectionOrAttachInputs(bool attachesInput)
    {
        using var data = TemporaryDirectory.Create();
        await using var host = new FixedDataDirectoryApplicationFactory(data.Path, configureServices: services => RegisterDormant(services));
        using var scope = host.Services.CreateScope();
        var drafts = scope.ServiceProvider.GetRequiredService<ConnectionDraftStore>();
        var saved = drafts.Write(null, new(drafts.Read().Version, "unrouted", "Wire", ConnectionKind.Module,
            Module: new("wire", 1, JsonSerializer.SerializeToElement(new { host = "127.0.0.1", port = 19001 }))), "fixture", null);
        var compiled = RoutePlanCompiler.Compile(scope.ServiceProvider.GetRequiredService<RouteDraftStore>().ReadCompilation(saved.Version, Token), Token);
        var transport = host.Services.GetRequiredService<TestWireOutputTransport>();
        if (attachesInput)
        { transport.TargetInputsOverride = ["meter/source"]; }
        else
        { transport.TargetConnectionIdOverride = 9999; }
        var runtime = host.Services.GetRequiredService<RuntimeHost>();
        await using var prepared = runtime.Prepare(new(1, [Input(), .. compiled.Document.Units]));
        Assert.Single(prepared.Failures);
        Assert.Empty(prepared.Outputs);
        Assert.Equal(0, transport.Adapters);
        Assert.Equal(0, transport.Sessions);
    }

    [Theory]
    [InlineData("connection")]
    [InlineData("transport")]
    [InlineData("queueBinding")]
    public async Task RemovingLastRouteKeepsFrozenSenderThroughMissingTransportAndNormalHostRestarts(string unavailable)
    {
        using var data = TemporaryDirectory.Create();
        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        var port = ((IPEndPoint)server.LocalEndpoint).Port;
        var settings = new Dictionary<string, string>
        { ["TenonEdge:Outbound:LocalPorts:0"] = port.ToString(CultureInfo.InvariantCulture) };
        long id;
        StoredDelivery original;
        string hash;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(45));
        await using (var host = new FixedDataDirectoryApplicationFactory(data.Path, settings, services => RegisterDormant(services)))
        {
            using var scope = host.Services.CreateScope();
            var connections = scope.ServiceProvider.GetRequiredService<ConnectionDraftStore>();
            var connection = connections.Write(null, new(connections.Read().Version, "wire", "Wire", ConnectionKind.Module,
                Module: new("wire", 1, JsonSerializer.SerializeToElement(new { host = "127.0.0.1", port }))), "fixture", null);
            id = connection.Connection.Id;
            var device = scope.ServiceProvider.GetRequiredService<DeviceDraftStore>().Write(null,
                new(connection.Version, "meter", "Meter", DeviceKind.Simulator), "fixture", null);
            var input = scope.ServiceProvider.GetRequiredService<InputDraftStore>().Write(device.Device.Id, null,
                new(device.Version, "source", "Source", Simulator: new(PeriodMs: 3600000, SampleJson: "{\"value\":9007199254740993}")), "fixture", null);
            var graph = new RouteGraph(1,
                [new("in", "input.simulator", 1, "Input", JsonSerializer.SerializeToElement(new { sourceIds = new[] { input.Input.Id } })),
                 new("out", "output.wiremodule", 1, "Output", JsonSerializer.SerializeToElement(new
                 { connectionId = id, parameters = new { address = "old/{deviceKey}" }, connections = new { connection = id } }))],
                [new("in", "out")], new([]));
            var routes = scope.ServiceProvider.GetRequiredService<RouteDraftStore>();
            var route = routes.Write(null, new(input.Version, "route", "Route", true, graph), "fixture", null);
            var coordinator = host.Services.GetRequiredService<RuntimeCoordinator>();
            var first = await coordinator.ApplyDraftAsync(route.Version, "fixture", null, Token);
            Assert.True(first.Activated, first.Code);
            Assert.Equal(1, first.Revision);
            var runtime = host.Services.GetRequiredService<RuntimeHost>();
            Assert.Single(runtime.GetOutputSenders()); // Routed target must not also get a queue-only sender.
            using var socket = await server.AcceptTcpClientAsync(deadline.Token);
            var frame = await ReadFrameAsync(socket.GetStream(), deadline.Token);
            original = Assert.Single(runtime.Deliveries.ReadPending(id, 0, 10));
            Assert.Equal(original.Payload.ToArray(), frame.Payload);
            Assert.Equal("old/meter", frame.Address);
            await socket.GetStream().WriteAsync(new byte[] { 21 }, deadline.Token);
            await OutputPipelineTests.UntilAsync(() => runtime.GetOutputSenders().Single().Sender.State == "automaticallyPaused");
            host.Services.GetRequiredService<OutputControlStore>().Write(id, new(true, true), "test", null);
            var disabled = routes.Write(route.Route.Id, new(route.Version, "route", "Route", false, graph), "fixture", null);
            var applied = await coordinator.ApplyDraftAsync(disabled.Version, "fixture", null, Token);
            Assert.True(applied.Activated, applied.Code);
            Assert.Equal(2, applied.Revision);
            var versions = host.Services.GetRequiredService<RuntimeVersionStore>();
            var frozen = versions.Load(2)!;
            hash = frozen.ContentHash;
            // An unapplied address edit must neither bypass pending protection nor redirect recovery.
            var changed = connections.Write(id, new(connections.Read().Version, "wire", "Wire", ConnectionKind.Module,
                Module: new("wire", 1, JsonSerializer.SerializeToElement(new { host = "127.0.0.1", port = port == 65535 ? port - 1 : port + 1 }))), "fixture", null);
            var rejected = await coordinator.ApplyDraftAsync(changed.Version, "fixture", null, Token);
            Assert.False(rejected.Committed);
            Assert.Equal(2, versions.ReadPointers().EffectiveRevision);
            var transport = host.Services.GetRequiredService<TestWireOutputTransport>();
            var bindings = transport.QueueBindings;
            transport.QueueBindingThrows = true;
            // Frozen execution and transaction guards must not invoke installed extensions.
            var execution = RuntimeSnapshotDocument.ReadExecutionPlan(frozen);
            Assert.Equal("queue/wire/sender", Assert.Single(execution.Units).Id.Key);
            Assert.Null(versions.TargetChangeProblem(execution));
            Assert.Equal(bindings, transport.QueueBindings);
            Assert.Equal(hash, versions.Load(2)!.ContentHash);
            Assert.Equal(original.Payload.ToArray(), Assert.Single(runtime.Deliveries.ReadPending(id, 0, 10)).Payload.ToArray());
        }
        await using (var broken = new FixedDataDirectoryApplicationFactory(data.Path, settings, services =>
        {
            RegisterDormant(services, unavailable);
            if (unavailable == "queueBinding")
            {
                services.AddSingleton(provider => new TestWireOutputTransport(provider.GetRequiredService<ISharedConnections>(),
                    provider.GetRequiredService<IOutboundConnector>())
                { QueueBindingThrows = true });
            }
        }))
        {
            var runtime = broken.Services.GetRequiredService<RuntimeHost>();
            Assert.Equal(RuntimeCoordinatorState.Active, broken.Services.GetRequiredService<RuntimeCoordinator>().Status.State);
            var status = runtime.GetStatus().Units;
            Assert.Equal(RuntimeUnitState.Running, Assert.Single(status, unit => unit.Id.Kind == "delivery.writer").State);
            var failure = Assert.Single(status, unit => unit.Id.Kind == "output.wire");
            Assert.Equal(RuntimeUnitState.BuildFailed, failure.State);
            Assert.DoesNotContain("fixture-secret", JsonSerializer.Serialize(failure), StringComparison.Ordinal);
            var pending = Assert.Single(runtime.Deliveries.ReadPending(id, 0, 10));
            Assert.Equal(original.DeliveryId, pending.DeliveryId);
            Assert.Equal(original.Payload.ToArray(), pending.Payload.ToArray());
            Assert.Equal(original.TargetAddress, pending.TargetAddress);
            Assert.Equal(hash, broken.Services.GetRequiredService<RuntimeVersionStore>().Load(2)!.ContentHash);
            Assert.False(server.Pending());
        }
        // Formatting module is now absent: an already-frozen queue needs only its transport and connection.
        await using var restored = new FixedDataDirectoryApplicationFactory(data.Path, settings, services => RegisterDormant(services, "module"));
        var active = restored.Services.GetRequiredService<RuntimeHost>();
        Assert.Single(active.GetOutputSenders());
        var controls = restored.Services.GetRequiredService<OutputControlStore>();
        Assert.True(controls.Read(id).ManuallyPaused);
        controls.Write(id, new(false, true, controls.Read(id).ResumeVersion), "test", null);
        using var replay = await server.AcceptTcpClientAsync(deadline.Token);
        var actual = await ReadFrameAsync(replay.GetStream(), deadline.Token);
        Assert.Equal(original.Payload.ToArray(), actual.Payload);
        Assert.Equal("old/meter", actual.Address);
        Assert.Equal(original.DeliveryId, JsonSerializer.Deserialize<JsonElement>(actual.Payload).GetProperty("messageId").GetGuid());
        await replay.GetStream().WriteAsync(new byte[] { 6 }, deadline.Token);
        await OutputPipelineTests.UntilAsync(() => active.Deliveries.ReadPending(id, 0, 10).Count == 0);
        Assert.Equal(hash, restored.Services.GetRequiredService<RuntimeVersionStore>().Load(2)!.ContentHash);
    }

    [Fact]
    public async Task UnroutedExtensionConnectionAcceptsExplicitMigrationAndSendsOriginalFrozenBytes()
    {
        using var data = TemporaryDirectory.Create();
        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        var port = ((IPEndPoint)server.LocalEndpoint).Port;
        await using var host = new FixedDataDirectoryApplicationFactory(data.Path, new Dictionary<string, string>
        { ["TenonEdge:Outbound:LocalPorts:0"] = port.ToString(CultureInfo.InvariantCulture) }, services => RegisterDormant(services));
        using var scope = host.Services.CreateScope();
        var drafts = scope.ServiceProvider.GetRequiredService<ConnectionDraftStore>();
        var saved = drafts.Write(null, new(drafts.Read().Version, "unrouted", "Wire", ConnectionKind.Module,
            Module: new("wire", 1, JsonSerializer.SerializeToElement(new { host = "127.0.0.1", port }))), new("operator"), null);
        var id = saved.Connection.Id;
        var snapshot = scope.ServiceProvider.GetRequiredService<RouteDraftStore>().ReadCompilation(saved.Version, Token);
        var compiled = RoutePlanCompiler.Compile(snapshot, Token);
        var queue = Assert.Single(compiled.Document.Units);
        Assert.Equal("output.wire", queue.Id.Kind);
        Assert.Equal("queue/unrouted/sender", queue.Id.Key);
        Assert.Empty(compiled.Topology.Paths);
        var runtime = host.Services.GetRequiredService<RuntimeHost>();
        var controls = host.Services.GetRequiredService<OutputControlStore>();
        controls.Write(1, new(true, true), "test", null);
        var pause = controls.Write(id, new(true, true), "test", null);
        await using var prepared = runtime.Prepare(new(1, [Input(), new TestWireOutputModule().Unit(), queue]));
        Assert.Empty(prepared.Failures);
        Assert.Empty(prepared.Outputs.Single(output => output.Target.ConnectionId == id).Target.Inputs);
        var target = prepared.CaptureMigrationTarget(id)!;
        var at = DateTimeOffset.UtcNow;
        var messageId = Guid.NewGuid();
        var sample = new EdgeMessage(Guid.NewGuid(), "source", "meter", at, TimeSource.Gateway, at,
            [new("value", FieldValue.FromInt64(9007199254740993))]);
        var message = prepared.Outputs.Single(output => output.Target.ConnectionId == 1).Target.CreateMessage(sample, messageId);
        await runtime.ActivateAsync(prepared);
        Assert.True((await runtime.Deliveries.EnqueueAsync([new(messageId, 1, sample.SourceMessageId, "meter", 1,
            message.Payload, message.Fingerprint, message.FingerprintSummary, message.Address)], Token)).Committed);
        var original = Assert.Single(runtime.Deliveries.ReadPending(1, 0, 10));
        var command = DeliveryMigrationCommand.Selected(host.Services.GetRequiredService<GatewayIdentity>().ConfigStoreId,
            Guid.NewGuid(), 1, id, target.Fingerprint, target.Summary,
            [new(original.Seq, original.DeliveryId, original.Fingerprint, original.MigrationId)]);
        var migrated = await runtime.MigrateDeliveriesAsync(command, 1, Token);
        Assert.True(migrated.Committed, migrated.Error);
        Assert.Empty(runtime.Deliveries.ReadPending(1, 0, 10));
        var retained = Assert.Single(runtime.Deliveries.ReadPending(id, 0, 10));
        Assert.Equal(original.Seq, retained.Seq);
        Assert.Equal(original.DeliveryId, retained.DeliveryId);
        Assert.Equal(original.Payload.ToArray(), retained.Payload.ToArray());
        Assert.Equal(original.TargetAddress, retained.TargetAddress);
        Assert.False(server.Pending());
        controls.Write(id, new(false, true, pause.ResumeVersion), "test", null);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        using var socket = await server.AcceptTcpClientAsync(deadline.Token);
        var actual = await ReadFrameAsync(socket.GetStream(), deadline.Token);
        Assert.Equal("events/meter", actual.Address);
        Assert.Equal(original.Payload.ToArray(), actual.Payload);
        Assert.Single(runtime.Deliveries.ReadPending(id, 0, 10));
        await socket.GetStream().WriteAsync(new byte[] { 6 }, deadline.Token);
        await OutputPipelineTests.UntilAsync(() => runtime.Deliveries.ReadPending(id, 0, 10).Count == 0);
    }
}
