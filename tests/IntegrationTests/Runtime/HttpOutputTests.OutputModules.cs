using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Plans;
using TenonEdge.Host.Features.Deliveries;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Storage;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.Runtime.Deliveries;
using TenonEdge.Runtime.Inputs;
using TenonEdge.Runtime.Lifecycle;
using TenonEdge.Runtime.Outputs;

namespace TenonEdge.IntegrationTests.Runtime;

public sealed partial class HttpOutputTests
{
    [Theory]
    [InlineData("missing")]
    [InlineData("version")]
    [InlineData("binding")]
    public async Task OutputModuleUsesOriginalDurableHttpSenderAndMissingModulePreservesFrozenQueue(string unavailable)
    {
        using var data = TemporaryDirectory.Create();
        using var seeds = TemporaryDirectory.Create();
        using var server = Listen();
        var module = new TestProtocolOutputModule("http");
        var output = module.Unit(JsonSerializer.SerializeToElement(new { baseAddress = Base(server) }), "data", inputs: ["meter/module"]);
        var sourcePlan = RuntimePlanDocument.Parse(InputModuleRuntimeTests.Document(Base(server)), inputs: new InputModuleCatalog([new TestScalarInputModule()]));
        var seed = Path.Combine(seeds.Path, "seed.json");
        await File.WriteAllBytesAsync(seed, TestProtocolOutputModule.Document([.. sourcePlan.Units.Where(unit => unit.Id.Kind.StartsWith("input.", StringComparison.Ordinal)), output]), Token);
        var settings = new Dictionary<string, string> { ["TenonEdge:Outbound:LocalPorts:0"] = ((IPEndPoint)server.LocalEndpoint).Port.ToString(CultureInfo.InvariantCulture) };
        (string Header, byte[] Body) original;
        Guid deliveryId;
        string hash;
        var first = ServeOnceAsync(server, 401);
        await using (var host = new FixedDataDirectoryApplicationFactory(data.Path,
            new Dictionary<string, string>(settings) { [RuntimeSettings.SeedSnapshotKey] = seed }, services =>
            {
                services.AddSingleton<IInputModuleFactory>(new TestScalarInputModule { UsesDurableHandoff = true });
                services.AddSingleton<IOutputModuleFactory>(module);
            }))
        {
            var runtime = host.Services.GetRequiredService<RuntimeHost>();
            Assert.Same(host.Services.GetRequiredService<OutputModuleCatalog>(), runtime.OutputModules);
            original = await first.WaitAsync(TimeSpan.FromSeconds(10), Token);
            await OutputPipelineTests.UntilAsync(() => runtime.Deliveries.ReadPending(1, 0, 2).Count == 1);
            var stored = Assert.Single(runtime.Deliveries.ReadPending(1, 0, 2));
            deliveryId = stored.DeliveryId;
            Assert.Equal(stored.Payload.ToArray(), original.Body);
            using var body = JsonDocument.Parse(original.Body);
            Assert.Equal("9007199254740993", body.RootElement.GetProperty("values").GetProperty("value").GetRawText());
            Assert.Contains(runtime.GetStatus().Units, unit => unit.Id == output.Id && unit.State == RuntimeUnitState.Running);
            Assert.Equal("http", runtime.CaptureMigrationTarget(1, 1)!.Recipient.Protocol);
            hash = host.Services.GetRequiredService<RuntimeVersionStore>().Load(1)!.ContentHash;
        }
        var broken = new TestProtocolOutputModule("http") { Version = unavailable == "version" ? 2 : 1, Throw = unavailable == "binding" };
        await using (var host = new FixedDataDirectoryApplicationFactory(data.Path, settings, services =>
        {
            services.AddSingleton<IInputModuleFactory>(new TestScalarInputModule { Emit = false });
            if (unavailable != "missing")
            { services.AddSingleton<IOutputModuleFactory>(broken); }
        }))
        {
            var runtime = host.Services.GetRequiredService<RuntimeHost>();
            Assert.Equal(RuntimeCoordinatorState.Active, host.Services.GetRequiredService<RuntimeCoordinator>().Status.State);
            var failure = Assert.Single(runtime.GetStatus().Units, unit => unit.Id == output.Id);
            Assert.Equal(RuntimeUnitState.BuildFailed, failure.State);
            Assert.DoesNotContain("fixture-secret", JsonSerializer.Serialize(failure), StringComparison.Ordinal);
            await OutputPipelineTests.UntilAsync(() => runtime.GetStatus().Units.Any(unit => unit.Id.Kind == "input.simulator" && unit.Reads?.ReadsCompleted >= 1));
            Assert.Equal(original.Body, Assert.Single(runtime.Deliveries.ReadPending(1, 0, 2)).Payload.ToArray());
            Assert.False(server.Pending());
            var versions = host.Services.GetRequiredService<RuntimeVersionStore>();
            Assert.Equal(hash, versions.Load(1)!.ContentHash);
            Assert.Throws<RuntimePlanDocumentException>(() => versions.ReadRetryCheckpoint(1, hash));
        }
        var replay = ServeOnceAsync(server, 204);
        await using (var host = new FixedDataDirectoryApplicationFactory(data.Path, settings, services =>
        {
            services.AddSingleton<IInputModuleFactory>(new TestScalarInputModule { Emit = false });
            services.AddSingleton<IOutputModuleFactory>(new TestProtocolOutputModule("http"));
        }))
        {
            var runtime = host.Services.GetRequiredService<RuntimeHost>();
            var received = await replay.WaitAsync(TimeSpan.FromSeconds(10), Token);
            Assert.Equal(original.Body, received.Body);
            Assert.Contains($"Idempotency-Key: {deliveryId:D}", received.Header, StringComparison.OrdinalIgnoreCase);
            await OutputPipelineTests.UntilAsync(() => runtime.Deliveries.ReadPending(1, 0, 2).Count == 0);
            Assert.Equal(hash, host.Services.GetRequiredService<RuntimeVersionStore>().Load(1)!.ContentHash);
        }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeAndModuleTargetsMigrateThroughOriginalPausedSendersWithoutReformatting(bool moduleTarget)
    {
        using var data = TemporaryDirectory.Create();
        using var server = Listen();
        var module = new TestProtocolOutputModule("http");
        await using var host = new FixedDataDirectoryApplicationFactory(data.Path, new Dictionary<string, string>
        { ["TenonEdge:Outbound:LocalPorts:0"] = ((IPEndPoint)server.LocalEndpoint).Port.ToString(CultureInfo.InvariantCulture) }, services =>
        {
            services.AddSingleton<IInputModuleFactory>(new TestScalarInputModule { Emit = false });
            services.AddSingleton<IOutputModuleFactory>(module);
        });
        var runtime = host.Services.GetRequiredService<RuntimeHost>();
        var controls = host.Services.GetRequiredService<OutputControlStore>();
        controls.Write(1, new(true, true), "test", null);
        var targetPause = controls.Write(2, new(true, true), "test", null);
        var input = RuntimePlanDocument.Parse(InputModuleRuntimeTests.Document(Base(server)), inputs: runtime.InputModules)
            .Units.Single(unit => unit.Id.Kind == "input.testscalar");
        var source = module.Unit(JsonSerializer.SerializeToElement(new { baseAddress = Base(server) + "old/" }), "frozen-path", 1, "route/source", "meter/module");
        var target = module.Unit(JsonSerializer.SerializeToElement(new { baseAddress = Base(server) + "new/" }), "new-format-path", 2, "route/target", "meter/module");
        if (moduleTarget)
        { source = OutputModuleCatalog.ReadProtocol(source)!; }
        else
        { target = OutputModuleCatalog.ReadProtocol(target)!; }
        await using var prepared = runtime.Prepare(new(1, [input, source, target]));
        Assert.Empty(prepared.Failures);
        var identity = prepared.CaptureMigrationTarget(2)!;
        Assert.Equal("http", identity.Recipient.Protocol);
        var deliveryId = Guid.NewGuid();
        var message = prepared.Outputs.Single(output => output.Target.ConnectionId == 1).Target.CreateMessage(Message(), deliveryId);
        await runtime.ActivateAsync(prepared);
        Assert.Equal(identity, runtime.CaptureMigrationTarget(2, 1));
        var frozen = new DeliveryDraft(deliveryId, 1, Guid.NewGuid(), "meter", 1, message.Payload, message.Fingerprint, message.FingerprintSummary,
            message.Address, message.QualityOfService, message.Headers, protocolMetadata: message.ProtocolMetadata);
        Assert.True((await runtime.Deliveries.EnqueueAsync([frozen], Token)).Committed);
        var before = Assert.Single(runtime.Deliveries.ReadPending(1, 0, 10));
        var command = DeliveryMigrationCommand.Selected(host.Services.GetRequiredService<GatewayIdentity>().ConfigStoreId, Guid.NewGuid(), 1, 2,
            identity.Fingerprint, identity.Summary, [new(before.Seq, before.DeliveryId, before.Fingerprint, before.MigrationId)]);
        var result = await runtime.MigrateDeliveriesAsync(command, 1, Token);
        Assert.True(result.Committed, result.Error);
        var after = Assert.Single(runtime.Deliveries.ReadPending(2, 0, 10));
        Assert.Equal(before.Payload.ToArray(), after.Payload.ToArray());
        Assert.Equal(before.TargetAddress, after.TargetAddress);
        Assert.Equal(before.DeliveryId, after.DeliveryId);
        Assert.True(controls.Read(2).ManuallyPaused);
        Assert.False(server.Pending());
        var request = ServeOnceAsync(server, 204);
        controls.Write(2, new(false, true, targetPause.ResumeVersion), "test", null);
        var actual = await request.WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.Equal(before.Payload.ToArray(), actual.Body);
        Assert.Contains("POST /api/new/frozen-path ", actual.Header, StringComparison.Ordinal);
        Assert.Contains($"Idempotency-Key: {deliveryId:D}", actual.Header, StringComparison.OrdinalIgnoreCase);
        await OutputPipelineTests.UntilAsync(() => runtime.Deliveries.ReadPending(2, 0, 10).Count == 0);
    }

}
