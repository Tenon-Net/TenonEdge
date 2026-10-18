using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Plans;
using TenonEdge.Host.Features.Configuration.Transfer;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Routes;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Security;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.IntegrationTests.Routes;
using TenonEdge.Runtime.Deliveries;
using TenonEdge.Runtime.Lifecycle;

namespace TenonEdge.IntegrationTests.Runtime;

public sealed partial class HttpOutputTests
{
    [Fact]
    public async Task OutputModulePreviewConfirmationUsesOriginalHttpBytesOnceWithoutQueueing()
    {
        using var data = TemporaryDirectory.Create();
        using var server = Listen();
        var port = ((IPEndPoint)server.LocalEndpoint).Port;
        await using var host = new FixedDataDirectoryApplicationFactory(data.Path,
            configureServices: services => services.AddSingleton<IOutputModuleFactory>(new TestProtocolOutputModule("http")));
        var request = RouteOutputModuleTests.Seed(host.Services, "http", port);
        host.Services.GetRequiredService<OutboundPolicyStore>().Write(new([], [port]), "fixture", null);
        TestAccounts.Create(host.Services, "editor", ApiRequests.StrongPassword, UserRole.Configurator);
        using var client = await host.CreateLoggedInClientAsync("editor", Token);
        var (output, confirmation) = await RouteOutputModuleTests.PreviewAndPrepare(client, request);
        Assert.False(server.Pending());
        var receiving = ServeOnceAsync(server, 204);
        using var response = await client.PostJsonAsync("/api/edge-admin/v1/routes/test-send/confirm", confirmation, Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("http.response", (await response.ReadJsonAsync(Token)).GetProperty("code").GetString());
        var wire = await receiving.WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.Equal(Encoding.UTF8.GetBytes(output.GetProperty("body").GetString()!), wire.Body);
        Assert.Contains("\"scaled\":9007199254740994", Encoding.UTF8.GetString(wire.Body), StringComparison.Ordinal);
        Assert.StartsWith("POST /events HTTP/1.1\r\n", wire.Header, StringComparison.Ordinal);
        Assert.Contains($"Idempotency-Key: {output.GetProperty("deliveryId").GetGuid():D}", wire.Header, StringComparison.OrdinalIgnoreCase);
        using var duplicate = await client.PostJsonAsync("/api/edge-admin/v1/routes/test-send/confirm", confirmation, Token);
        Assert.Equal(HttpStatusCode.NotFound, duplicate.StatusCode);
        Assert.False(server.Pending());
        Assert.Equal(0, host.Services.GetRequiredService<DeliveryCache>().GetStatus().CommittedRecords);
        Assert.Null(host.Services.GetRequiredService<RuntimeVersionStore>().ReadPointers().EffectiveRevision);
    }

    [Fact]
    public async Task OutputModuleGraphAppliesAndRestartsWithFrozenHttpAndHistoricalDiagnosticLabels()
    {
        using var data = TemporaryDirectory.Create();
        using var server = Listen();
        var port = ((IPEndPoint)server.LocalEndpoint).Port;
        var settings = new Dictionary<string, string> { ["TenonEdge:Outbound:LocalPorts:0"] = port.ToString(CultureInfo.InvariantCulture) };
        StoredDelivery frozen;
        string hash;
        byte[] exported;
        var denied = ServeOnceAsync(server, 401);
        await using (var host = new FixedDataDirectoryApplicationFactory(data.Path, settings,
            services => services.AddSingleton<IOutputModuleFactory>(new TestProtocolOutputModule("http"))))
        {
            var seed = RouteOutputModuleTests.Seed(host.Services, "http", port);
            var saved = host.Services.GetRequiredService<RouteDraftStore>().Write(null,
                new(seed.ExpectedVersion, "module_route", "Module route", true, seed.Graph), "fixture", null);
            var applied = await host.Services.GetRequiredService<RuntimeCoordinator>().ApplyDraftAsync(saved.Version, "fixture", null, Token);
            Assert.True(applied.Activated);
            var received = await denied.WaitAsync(TimeSpan.FromSeconds(10), Token);
            var target = seed.Graph.Nodes[^1].Config.GetProperty("connectionId").GetInt64();
            var cache = host.Services.GetRequiredService<DeliveryCache>();
            await OutputPipelineTests.UntilAsync(() => cache.GetStatus().CommittedRecords == 1);
            frozen = Assert.Single(cache.ReadPending(target, 0, 10));
            Assert.Equal(frozen.Payload.ToArray(), received.Body);
            Assert.Contains("\"scaled\":9007199254740994", Encoding.UTF8.GetString(received.Body), StringComparison.Ordinal);
            hash = host.Services.GetRequiredService<RuntimeVersionStore>().Load(1)!.ContentHash;
            exported = host.Services.GetRequiredService<ConfigurationExportStore>().Export(saved.Version, Token);
        }
        await using (var missing = new FixedDataDirectoryApplicationFactory(data.Path, settings))
        {
            var runtime = missing.Services.GetRequiredService<RuntimeHost>();
            Assert.Equal(RuntimeCoordinatorState.Active, missing.Services.GetRequiredService<RuntimeCoordinator>().Status.State);
            Assert.Equal(RuntimeUnitState.BuildFailed, Assert.Single(runtime.GetStatus().Units, unit => unit.Id.Kind == "output.testhttp").State);
            await OutputPipelineTests.UntilAsync(() => runtime.GetStatus().Units.Any(unit => unit.Id.Kind == "input.simulator" && unit.Reads?.ReadsCompleted >= 1));
            Assert.Equal(frozen.Payload.ToArray(), Assert.Single(runtime.Deliveries.ReadPending(frozen.ConnectionId, 0, 10)).Payload.ToArray());
            Assert.False(server.Pending());
            var snapshot = missing.Services.GetRequiredService<RuntimeVersionStore>().Load(1)!;
            Assert.Equal(hash, snapshot.ContentHash);
            var execution = RuntimeSnapshotDocument.ReadForExecution(snapshot);
            var output = Assert.Single(execution.Plan.Units, unit => unit.Id.Kind == "output.testhttp");
            Assert.Equal(RuntimeUnitId.Create("output.testhttp", "module_route/out"), execution.Plan.ToPlan(1).DiagnosticObjects[output.Id]);
            Assert.Equal(exported, missing.Services.GetRequiredService<ConfigurationExportStore>().Export(null, Token));
        }
        var replay = ReceiveTwo();
        await using var restored = new FixedDataDirectoryApplicationFactory(data.Path, settings,
            services => services.AddSingleton<IOutputModuleFactory>(new TestProtocolOutputModule("http")));
        Assert.Equal(RuntimeCoordinatorState.Active, restored.Services.GetRequiredService<RuntimeCoordinator>().Status.State);
        var first = await replay.WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.Equal(frozen.Payload.ToArray(), first.Body);
        Assert.Contains($"Idempotency-Key: {frozen.DeliveryId:D}", first.Header, StringComparison.OrdinalIgnoreCase);
        var restoredCache = restored.Services.GetRequiredService<DeliveryCache>();
        await OutputPipelineTests.UntilAsync(() => restoredCache.GetStatus().DeletedRecords == 2);
        Assert.Empty(restoredCache.ReadPending(frozen.ConnectionId, 0, 10));
        Assert.Equal(hash, restored.Services.GetRequiredService<RuntimeVersionStore>().Load(1)!.ContentHash);

        async Task<(string Header, byte[] Body)> ReceiveTwo()
        {
            var first = await ServeOnceAsync(server, 204);
            var second = await ServeOnceAsync(server, 204);
            Assert.Contains("\"scaled\":9007199254740994", Encoding.UTF8.GetString(second.Body), StringComparison.Ordinal);
            Assert.NotEqual(first.Body, second.Body); // The restarted source creates a new message; the old record is not reformatted.
            return first;
        }
    }
}
