using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.Runtime.Inputs;
using TenonEdge.Runtime.Lifecycle;

namespace TenonEdge.IntegrationTests.Runtime;

public sealed partial class HttpOutputTests
{
    [Theory]
    [InlineData("missing")]
    [InlineData("version")]
    [InlineData("description")]
    [InlineData("build")]
    public async Task RegisteredInputUsesOriginalHostAndHttpQueueUnavailableModuleOnlyStopsItsOwnUnit(string unavailable)
    {
        using var data = TemporaryDirectory.Create();
        using var seeds = TemporaryDirectory.Create();
        using var server = Listen();
        var seed = Path.Combine(seeds.Path, "seed.json");
        await File.WriteAllBytesAsync(seed, InputModuleRuntimeTests.Document(Base(server)), Token);
        var settings = new Dictionary<string, string>
        {
            ["TenonEdge:Outbound:LocalPorts:0"] = ((IPEndPoint)server.LocalEndpoint).Port.ToString(CultureInfo.InvariantCulture),
        };
        var module = new TestScalarInputModule { UsesDurableHandoff = true };
        string snapshotHash;
        (string Header, byte[] Body) original;
        Guid deliveryId;
        var firstRequest = ServeOnceAsync(server, 401);
        await using (var host = new FixedDataDirectoryApplicationFactory(data.Path,
            new Dictionary<string, string>(settings) { [RuntimeSettings.SeedSnapshotKey] = seed }, services => services.AddSingleton<IInputModuleFactory>(module)))
        {
            var runtime = host.Services.GetRequiredService<RuntimeHost>();
            Assert.Same(host.Services.GetRequiredService<InputModuleCatalog>(), runtime.InputModules);
            original = await firstRequest.WaitAsync(TimeSpan.FromSeconds(10), Token);
            await OutputPipelineTests.UntilAsync(() => runtime.Deliveries.ReadPending(1, 0, 2).Count == 1);
            var record = Assert.Single(runtime.Deliveries.ReadPending(1, 0, 2));
            deliveryId = record.DeliveryId;
            Assert.Equal(record.Payload.ToArray(), original.Body);
            using var body = JsonDocument.Parse(original.Body);
            Assert.Equal("9007199254740993", body.RootElement.GetProperty("values").GetProperty("value").GetRawText());
            Assert.Equal(1, module.Builds);
            Assert.Equal(1, module.Starts);
            Assert.Equal(InputHandoffStatus.Committed, await module.Handoff.Task.WaitAsync(TimeSpan.FromSeconds(10), Token));
            snapshotHash = host.Services.GetRequiredService<RuntimeVersionStore>().Load(1)!.ContentHash;
        }
        Assert.Equal(1, module.Disposed);
        var pendingReplay = ServeOnceAsync(server, 401);
        var broken = new TestScalarInputModule
        { Version = unavailable == "version" ? 2 : 3, ThrowDescription = unavailable == "description", ThrowBuild = unavailable == "build" };
        await using (var missing = new FixedDataDirectoryApplicationFactory(data.Path, settings, services =>
        {
            if (unavailable != "missing")
            { services.AddSingleton<IInputModuleFactory>(broken); }
        }))
        {
            var coordinator = missing.Services.GetRequiredService<RuntimeCoordinator>();
            Assert.Equal(RuntimeCoordinatorState.Active, coordinator.Status.State);
            var runtime = missing.Services.GetRequiredService<RuntimeHost>();
            var failed = Assert.Single(runtime.GetStatus().Units, unit => unit.Id.Kind == module.Kind);
            Assert.Equal(RuntimeUnitState.BuildFailed, failed.State);
            Assert.DoesNotContain("fixture-secret", JsonSerializer.Serialize(failed), StringComparison.Ordinal);
            Assert.Equal(0, broken.Starts);
            await OutputPipelineTests.UntilAsync(() => runtime.GetStatus().Units.Any(unit => unit.Id.Kind == "input.simulator" && unit.Reads?.ReadsCompleted >= 1));
            var replayed = await pendingReplay.WaitAsync(TimeSpan.FromSeconds(10), Token);
            Assert.Equal(original.Body, replayed.Body);
            Assert.Contains($"Idempotency-Key: {deliveryId:D}", replayed.Header, StringComparison.OrdinalIgnoreCase);
            var store = missing.Services.GetRequiredService<RuntimeVersionStore>();
            Assert.Equal(snapshotHash, RuntimeSnapshotDocument.ReadPlan(store.Load(1)!).ContentHash);
            if (unavailable != "build")
            { Assert.Throws<RuntimePlanDocumentException>(() => store.ReadRetryCheckpoint(1, snapshotHash)); }
            Assert.Equal(original.Body, Assert.Single(missing.Services.GetRequiredService<RuntimeHost>().Deliveries.ReadPending(1, 0, 2)).Payload.ToArray());
        }
        Assert.False(server.Pending());
        var restored = new TestScalarInputModule { Emit = false };
        var replay = ServeOnceAsync(server, 204);
        await using (var host = new FixedDataDirectoryApplicationFactory(data.Path, settings, services => services.AddSingleton<IInputModuleFactory>(restored)))
        {
            var runtime = host.Services.GetRequiredService<RuntimeHost>(); // WebApplicationFactory starts lazily.
            var received = await replay.WaitAsync(TimeSpan.FromSeconds(10), Token);
            Assert.Equal(original.Body, received.Body);
            Assert.Contains($"Idempotency-Key: {deliveryId:D}", received.Header, StringComparison.OrdinalIgnoreCase);
            await OutputPipelineTests.UntilAsync(() => runtime.Deliveries.ReadPending(1, 0, 2).Count == 0);
            Assert.Equal(snapshotHash, host.Services.GetRequiredService<RuntimeVersionStore>().Load(1)!.ContentHash);
            Assert.Equal(RuntimeCoordinatorState.Active, host.Services.GetRequiredService<RuntimeCoordinator>().Status.State);
        }
        Assert.Equal(1, restored.Builds);
        Assert.Equal(1, restored.Starts);
        Assert.Equal(1, restored.Disposed);
    }
}
