using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Plans;
using TenonEdge.Host.Features.Configuration.Transfer;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Inputs;
using TenonEdge.Host.Features.Routes;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Security;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.IntegrationTests.Inputs;
using TenonEdge.Runtime.Deliveries;
using TenonEdge.Runtime.Lifecycle;

namespace TenonEdge.IntegrationTests.Runtime;

public sealed partial class HttpOutputTests
{
    [Fact]
    public async Task ModuleDraftCompilesPreviewsAppliesAndRestartsWithoutReprocessingFrozenHttp()
    {
        using var data = TemporaryDirectory.Create();
        using var receiver = Listen();
        var port = ((IPEndPoint)receiver.LocalEndpoint).Port;
        var settings = new Dictionary<string, string> { ["TenonEdge:Outbound:LocalPorts:0"] = port.ToString(CultureInfo.InvariantCulture) };
        var module = new TestScalarInputModule();
        var denied = ServeOnceAsync(receiver, 401);
        StoredDelivery frozen;
        string hash;
        byte[] exported;
        await using (var host = new FixedDataDirectoryApplicationFactory(data.Path, settings,
            services => services.AddSingleton<IInputModuleFactory>(module)))
        {
            var connection = host.Services.GetRequiredService<ConnectionDraftStore>().Write(null,
                new("0", "sink", "Sink", ConnectionKind.Http, Http: new(Base(receiver))), "test", null);
            var device = InputModuleDraftTests.Device(host.Services, connection.Version);
            var input = host.Services.GetRequiredService<InputDraftStore>().Write(device.Device.Id, null,
                InputModuleDraftTests.Request(device.Version), "test", null);
            var graph = InputModuleDraftTests.Graph(input.Input.Id, connection.Connection.Id);
            var routes = host.Services.GetRequiredService<RouteDraftStore>();
            var saved = routes.Write(null, new(input.Version, "module_route", "Module route", true, graph), "test", null);
            var compilation = routes.ReadCompilation(saved.Version, Token);
            var compiled = RoutePlanCompiler.Compile(compilation, Token);
            Assert.Contains(compiled.Document.Units, unit => unit.Id == RuntimeUnitId.Create(module.Kind, "meter/source"));
            var sample = RoutePreviewSample.Decode(compilation, input.Input.Id, "{\"value\":9007199254740993}",
                DateTimeOffset.UtcNow, ClockSyncState.Unknown, Token);
            var preview = RouteSamplePreview.Run(compilation, input.Input.Id, sample.Message, Token);
            Assert.Contains("9007199254740993", Assert.Single(preview.Paths).Output!.Body, StringComparison.Ordinal);
            Assert.Equal(0, module.Builds);
            Assert.Equal(0, module.Starts);
            exported = host.Services.GetRequiredService<ConfigurationExportStore>().Export(saved.Version, Token);
            var applied = await host.Services.GetRequiredService<RuntimeCoordinator>().ApplyDraftAsync(saved.Version, "test", null, Token);
            Assert.True(applied.Activated);
            var request = await denied.WaitAsync(TimeSpan.FromSeconds(10), Token);
            var runtime = host.Services.GetRequiredService<RuntimeHost>();
            await OutputPipelineTests.UntilAsync(() => runtime.Deliveries.GetStatus().CommittedRecords == 1);
            frozen = Assert.Single(runtime.Deliveries.ReadPending(connection.Connection.Id, 0, 10));
            Assert.Equal(frozen.Payload.ToArray(), request.Body);
            Assert.Contains("9007199254740993", System.Text.Encoding.UTF8.GetString(request.Body), StringComparison.Ordinal);
            var snapshot = host.Services.GetRequiredService<RuntimeVersionStore>().Load(1)!;
            Assert.Equal(2, snapshot.SchemaVersion);
            hash = snapshot.ContentHash;
            TestAccounts.Create(host.Services, "module-viewer", ApiRequests.StrongPassword, UserRole.Viewer);
            using var viewer = await host.CreateLoggedInClientAsync("module-viewer", Token);
            using var values = await viewer.GetAsync("/api/edge-admin/v1/runtime/devices/meter/inputs/source/values?revision=1", Token);
            Assert.Equal(HttpStatusCode.OK, values.StatusCode);
            using var current = JsonDocument.Parse(await values.Content.ReadAsStringAsync(Token));
            var point = Assert.Single(current.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal("count", point.GetProperty("unit").GetString());
            Assert.Equal("9007199254740993", point.GetProperty("value").GetProperty("text").GetString());
        }
        var replay = ServeOnceAsync(receiver, 204);
        await using (var missing = new FixedDataDirectoryApplicationFactory(data.Path, settings))
        {
            var coordinator = missing.Services.GetRequiredService<RuntimeCoordinator>();
            Assert.Equal(RuntimeCoordinatorState.Active, coordinator.Status.State);
            var runtime = missing.Services.GetRequiredService<RuntimeHost>();
            Assert.Equal(RuntimeUnitState.BuildFailed, Assert.Single(runtime.GetStatus().Units, unit => unit.Id.Kind == module.Kind).State);
            var received = await replay.WaitAsync(TimeSpan.FromSeconds(10), Token);
            Assert.Equal(frozen.Payload.ToArray(), received.Body);
            Assert.Contains($"Idempotency-Key: {frozen.DeliveryId:D}", received.Header, StringComparison.OrdinalIgnoreCase);
            await OutputPipelineTests.UntilAsync(() => runtime.Deliveries.GetStatus().DeletedRecords == 1);
            Assert.Equal(0, runtime.Deliveries.GetStatus().CommittedRecords);
            var snapshot = missing.Services.GetRequiredService<RuntimeVersionStore>().Load(1)!;
            Assert.Equal(hash, snapshot.ContentHash);
            var read = RuntimeSnapshotDocument.ReadForExecution(snapshot);
            var id = Assert.Single(read.Plan.Units, unit => unit.Id.Kind == "output.http").Id;
            Assert.Equal(RuntimeUnitId.Create("output.http", "module_route/out"), read.Plan.ToPlan(1).DiagnosticObjects[id]);
            Assert.Equal(exported, missing.Services.GetRequiredService<ConfigurationExportStore>().Export(null, Token));
            using var viewer = await missing.CreateLoggedInClientAsync("module-viewer", Token);
            using var values = await viewer.GetAsync("/api/edge-admin/v1/runtime/devices/meter/inputs/source/values?revision=1", Token);
            Assert.Equal(HttpStatusCode.OK, values.StatusCode);
            using var current = JsonDocument.Parse(await values.Content.ReadAsStringAsync(Token));
            var point = Assert.Single(current.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal("count", point.GetProperty("unit").GetString());
            Assert.NotEqual("good", point.GetProperty("value").GetProperty("quality").GetString());
        }
        var restored = new TestScalarInputModule();
        var fresh = ServeOnceAsync(receiver, 204);
        await using var restarted = new FixedDataDirectoryApplicationFactory(data.Path, settings,
            services => services.AddSingleton<IInputModuleFactory>(restored));
        Assert.Equal(RuntimeCoordinatorState.Active, restarted.Services.GetRequiredService<RuntimeCoordinator>().Status.State);
        var final = await fresh.WaitAsync(TimeSpan.FromSeconds(10), Token);
        using var body = JsonDocument.Parse(final.Body);
        Assert.Equal(9007199254740993L, body.RootElement.GetProperty("values").GetProperty("value").GetInt64());
        Assert.Equal(1, restored.Builds);
        Assert.Equal(1, restored.Starts);
        Assert.Equal(hash, restarted.Services.GetRequiredService<RuntimeVersionStore>().Load(1)!.ContentHash);
    }
}
