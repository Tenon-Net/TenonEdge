using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Host.Features.Deliveries;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Storage;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.Runtime.Deliveries;
using TenonEdge.Runtime.Lifecycle;

namespace TenonEdge.IntegrationTests.Runtime;

public sealed partial class ExtensionOutputRuntimeTests
{
    private static void RegisterDormant(IServiceCollection services, string? unavailable = null)
    {
        services.AddSingleton<IInputModuleFactory>(new TestScalarInputModule { Emit = false });
        if (unavailable != "connection")
        { services.AddSingleton(TestWireOutputModule.Definition); }
        if (unavailable != "module")
        { services.AddSingleton<IOutputModuleFactory>(new TestWireOutputModule { Throw = unavailable == "binding" }); }
        if (unavailable != "transport")
        {
            services.AddSingleton<TestWireOutputTransport>();
            services.AddSingleton<IOutputAdapterFactory>(provider => provider.GetRequiredService<TestWireOutputTransport>());
        }
    }

    [Theory]
    [InlineData("module")]
    [InlineData("connection")]
    [InlineData("transport")]
    [InlineData("binding")]
    public async Task MissingExtensionOnHostRestartPreservesFrozenWireRecordUntilOriginalModuleReturns(string unavailable)
    {
        using var data = TemporaryDirectory.Create();
        using var seeds = TemporaryDirectory.Create();
        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        var port = ((IPEndPoint)server.LocalEndpoint).Port;
        var settings = new Dictionary<string, string> { ["TenonEdge:Outbound:LocalPorts:0"] = port.ToString(CultureInfo.InvariantCulture) };
        var output = new TestWireOutputModule().Unit(port);
        var seed = Path.Combine(seeds.Path, "wire.json");
        await File.WriteAllBytesAsync(seed, TestProtocolOutputModule.Document(Input(), output), Token);
        WireFrame original;
        StoredDelivery before;
        string hash;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        await using (var first = new FixedDataDirectoryApplicationFactory(data.Path,
            new Dictionary<string, string>(settings) { [RuntimeSettings.SeedSnapshotKey] = seed }, Register))
        {
            var runtime = first.Services.GetRequiredService<RuntimeHost>();
            using var socket = await server.AcceptTcpClientAsync(deadline.Token);
            original = await ReadFrameAsync(socket.GetStream(), deadline.Token);
            await socket.GetStream().WriteAsync(new byte[] { 21 }, deadline.Token);
            await OutputPipelineTests.UntilAsync(() => runtime.GetOutputSenders().SingleOrDefault()?.Sender.State == "automaticallyPaused");
            before = Assert.Single(runtime.Deliveries.ReadPending(1, 0, 10));
            Assert.Equal(original.Payload, before.Payload.ToArray());
            Assert.Equal("events/meter", before.TargetAddress);
            using var body = JsonDocument.Parse(original.Payload);
            Assert.Equal("9007199254740993", body.RootElement.GetProperty("values").GetProperty("value").GetRawText());
            hash = first.Services.GetRequiredService<RuntimeVersionStore>().Load(1)!.ContentHash;
        }
        await using (var broken = new FixedDataDirectoryApplicationFactory(data.Path, settings, services => RegisterDormant(services, unavailable)))
        {
            var runtime = broken.Services.GetRequiredService<RuntimeHost>();
            Assert.Equal(RuntimeCoordinatorState.Active, broken.Services.GetRequiredService<RuntimeCoordinator>().Status.State);
            var failure = Assert.Single(runtime.GetStatus().Units, unit => unit.Id == output.Id);
            Assert.Equal(RuntimeUnitState.BuildFailed, failure.State);
            Assert.DoesNotContain("fixture-secret", JsonSerializer.Serialize(failure), StringComparison.Ordinal);
            await OutputPipelineTests.UntilAsync(() => ((TestScalarInputModule)Assert.Single(broken.Services.GetServices<IInputModuleFactory>())).Starts == 1);
            var retained = Assert.Single(runtime.Deliveries.ReadPending(1, 0, 10));
            Assert.Equal(before.DeliveryId, retained.DeliveryId);
            Assert.Equal(before.Payload.ToArray(), retained.Payload.ToArray());
            Assert.Equal(before.TargetAddress, retained.TargetAddress);
            Assert.Equal(hash, broken.Services.GetRequiredService<RuntimeVersionStore>().Load(1)!.ContentHash);
            Assert.False(server.Pending());
        }
        await using var restored = new FixedDataDirectoryApplicationFactory(data.Path, settings, services => RegisterDormant(services));
        var active = restored.Services.GetRequiredService<RuntimeHost>();
        using var replay = await server.AcceptTcpClientAsync(deadline.Token);
        var received = await ReadFrameAsync(replay.GetStream(), deadline.Token);
        Assert.Equal(original.Address, received.Address);
        Assert.Equal(original.Payload, received.Payload);
        using var replayBody = JsonDocument.Parse(received.Payload);
        Assert.Equal(before.DeliveryId, replayBody.RootElement.GetProperty("messageId").GetGuid());
        await replay.GetStream().WriteAsync(new byte[] { 6 }, deadline.Token);
        await OutputPipelineTests.UntilAsync(() => active.Deliveries.ReadPending(1, 0, 10).Count == 0);
        Assert.Equal(hash, restored.Services.GetRequiredService<RuntimeVersionStore>().Load(1)!.ContentHash);
    }

    [Fact]
    public async Task ExplicitMigrationUsesOriginalSendersAndSendsOldFrozenAddressNotTargetsCurrentTemplate()
    {
        using var data = TemporaryDirectory.Create();
        using var source = new TcpListener(IPAddress.Loopback, 0);
        using var target = new TcpListener(IPAddress.Loopback, 0);
        source.Start();
        target.Start();
        var sourcePort = ((IPEndPoint)source.LocalEndpoint).Port;
        var targetPort = ((IPEndPoint)target.LocalEndpoint).Port;
        await using var host = new FixedDataDirectoryApplicationFactory(data.Path, new Dictionary<string, string>
        {
            ["TenonEdge:Outbound:LocalPorts:0"] = sourcePort.ToString(CultureInfo.InvariantCulture),
            ["TenonEdge:Outbound:LocalPorts:1"] = targetPort.ToString(CultureInfo.InvariantCulture),
        }, services => RegisterDormant(services));
        var runtime = host.Services.GetRequiredService<RuntimeHost>();
        var controls = host.Services.GetRequiredService<OutputControlStore>();
        controls.Write(1, new(true, true), "test", null);
        var paused = controls.Write(2, new(true, true), "test", null);
        var module = new TestWireOutputModule();
        await using var prepared = runtime.Prepare(new(1,
            [Input(), module.Unit(sourcePort), module.Unit(targetPort, "new-format/{deviceKey}", 2, "route/new-target")]));
        Assert.Empty(prepared.Failures);
        var at = DateTimeOffset.UtcNow;
        var deliveryId = Guid.NewGuid();
        var sample = new EdgeMessage(Guid.NewGuid(), "source", "meter", at, TimeSource.Gateway, at,
            [new("value", FieldValue.FromInt64(9007199254740993))]);
        var message = prepared.Outputs.Single(output => output.Target.ConnectionId == 1).Target.CreateMessage(sample, deliveryId);
        var identity = prepared.CaptureMigrationTarget(2)!;
        await runtime.ActivateAsync(prepared);
        Assert.True((await runtime.Deliveries.EnqueueAsync([new(deliveryId, 1, sample.SourceMessageId, "meter", 1,
            message.Payload, message.Fingerprint, message.FingerprintSummary, message.Address)], Token)).Committed);
        var before = Assert.Single(runtime.Deliveries.ReadPending(1, 0, 10));
        var command = DeliveryMigrationCommand.Selected(host.Services.GetRequiredService<GatewayIdentity>().ConfigStoreId,
            Guid.NewGuid(), 1, 2, identity.Fingerprint, identity.Summary,
            [new(before.Seq, before.DeliveryId, before.Fingerprint, before.MigrationId)]);
        var result = await runtime.MigrateDeliveriesAsync(command, 1, Token);
        Assert.True(result.Committed, result.Error);
        var after = Assert.Single(runtime.Deliveries.ReadPending(2, 0, 10));
        Assert.Equal(before.Seq, after.Seq);
        Assert.Equal(before.DeliveryId, after.DeliveryId);
        Assert.Equal(before.Payload.ToArray(), after.Payload.ToArray());
        Assert.Equal(before.TargetAddress, after.TargetAddress);
        Assert.True(controls.Read(2).ManuallyPaused);
        Assert.False(source.Pending());
        Assert.False(target.Pending());
        controls.Write(2, new(false, true, paused.ResumeVersion), "test", null);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        using var socket = await target.AcceptTcpClientAsync(deadline.Token);
        var actual = await ReadFrameAsync(socket.GetStream(), deadline.Token);
        Assert.Equal("events/meter", actual.Address);
        Assert.Equal(before.Payload.ToArray(), actual.Payload);
        await socket.GetStream().WriteAsync(new byte[] { 6 }, deadline.Token);
        await OutputPipelineTests.UntilAsync(() => runtime.Deliveries.ReadPending(2, 0, 10).Count == 0);
        Assert.False(source.Pending());
    }

    [Fact]
    public async Task ExtensionCannotConnectToUnapprovedLocalPortAndKeepsOriginalPendingRecord()
    {
        using var data = TemporaryDirectory.Create();
        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        var port = ((IPEndPoint)server.LocalEndpoint).Port;
        await using var host = new FixedDataDirectoryApplicationFactory(data.Path, configureServices: Register);
        var runtime = host.Services.GetRequiredService<RuntimeHost>();
        await using var plan = runtime.Prepare(new(1, [Input(), new TestWireOutputModule().Unit(port)]));
        Assert.Empty(plan.Failures);
        await runtime.ActivateAsync(plan);
        await OutputPipelineTests.UntilAsync(() => runtime.GetOutputSenders().SingleOrDefault()?.Sender.State == "automaticallyPaused");
        Assert.False(server.Pending());
        var record = Assert.Single(runtime.Deliveries.ReadPending(1, 0, 10));
        Assert.Equal("events/meter", record.TargetAddress);
        Assert.Equal(0, record.FailureCount);
        Assert.Empty(runtime.Deliveries.ReadFailed(1, 0, 10));
    }
}
