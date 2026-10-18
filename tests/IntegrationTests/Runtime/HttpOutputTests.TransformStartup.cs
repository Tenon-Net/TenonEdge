using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Plans;
using TenonEdge.Host.Features.Devices;
using TenonEdge.Host.Features.Inputs;
using TenonEdge.Host.Features.Routes;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.IntegrationTests.Routes;
using TenonEdge.Runtime.Deliveries;
using TenonEdge.Runtime.Lifecycle;

namespace TenonEdge.IntegrationTests.Runtime;

public sealed partial class HttpOutputTests
{
    [Fact]
    public async Task AppliedGraphRestartsWithoutTransformAndPreservesFrozenPathsAndDiagnosticOwnership()
    {
        using var data = TemporaryDirectory.Create();
        using var server = Listen();
        var port = ((IPEndPoint)server.LocalEndpoint).Port;
        var settings = new Dictionary<string, string> { ["TenonEdge:Outbound:LocalPorts:0"] = port.ToString(CultureInfo.InvariantCulture) };
        var firstRequest = ServeOnceAsync(server, 401);
        StoredDelivery[] frozen;
        long connectionId;
        string hash;
        string version;
        await using (var host = new FixedDataDirectoryApplicationFactory(data.Path, settings,
            services => services.AddSingleton<IMessageTransformFactory>(new TestOffsetTransformFactory())))
        {
            var seeded = RoutePreviewTests.Seed(host.Services, port);
            connectionId = seeded.Graph.Nodes[1].Config.GetProperty("connectionId").GetInt64();
            var device = Assert.Single(host.Services.GetRequiredService<DeviceDraftStore>().Read().Devices);
            var sources = host.Services.GetRequiredService<InputDraftStore>();
            var input = Assert.Single(sources.Read(device.Id).Inputs);
            // One immediate sample per host start; no second cycle races the frozen FIFO assertions.
            var slow = sources.Write(device.Id, input.Id,
                new(seeded.ExpectedVersion, input.Key, input.Name, Simulator: input.Simulator! with { PeriodMs = 86400000 }), "test", null);
            var drafts = host.Services.GetRequiredService<RouteDraftStore>();
            var transformed = drafts.Write(null, new(slow.Version, "extended", "Extended", true, RouteTransformTests.Graph(seeded.Graph)), "test", null);
            var healthy = drafts.Write(null, new(transformed.Version, "healthy", "Healthy", true, seeded.Graph), "test", null);
            version = healthy.Version;
            var result = await host.Services.GetRequiredService<RuntimeCoordinator>().ApplyDraftAsync(version, "test", null, Token);
            Assert.True(result.Activated);
            var first = await firstRequest.WaitAsync(TimeSpan.FromSeconds(10), Token);
            var runtime = host.Services.GetRequiredService<RuntimeHost>();
            await OutputPipelineTests.UntilAsync(() => runtime.Deliveries.GetStatus().CommittedRecords == 2);
            frozen = [.. runtime.Deliveries.ReadPending(connectionId, 0, 10)];
            Assert.Equal(2, frozen.Length);
            Assert.Equal(frozen[0].Payload.ToArray(), first.Body);
            using var body = JsonDocument.Parse(first.Body);
            Assert.Equal(2, body.RootElement.GetProperty("values").GetProperty("final").GetInt32());
            var snapshot = host.Services.GetRequiredService<RuntimeVersionStore>().Load(1)!;
            Assert.Equal(2, snapshot.SchemaVersion);
            hash = snapshot.ContentHash;
        }

        async Task<(string Header, byte[] Body)[]> ReceiveAsync() =>
            [await ServeOnceAsync(server, 204), await ServeOnceAsync(server, 204), await ServeOnceAsync(server, 204)];
        var received = ReceiveAsync();
        await using var missing = new FixedDataDirectoryApplicationFactory(data.Path, settings);
        var active = missing.Services.GetRequiredService<RuntimeHost>();
        Assert.Equal(RuntimeCoordinatorState.Active, missing.Services.GetRequiredService<RuntimeCoordinator>().Status.State);
        var broken = Assert.Single(active.GetStatus().Units, unit => unit.Id.Kind == "output.http" && unit.Id.Key.StartsWith("extended/", StringComparison.Ordinal));
        Assert.Equal(RuntimeUnitState.BuildFailed, broken.State);
        var actual = await received.WaitAsync(TimeSpan.FromSeconds(10), Token);
        for (var index = 0; index < frozen.Length; index++)
        {
            Assert.Equal(frozen[index].Payload.ToArray(), actual[index].Body);
            Assert.Contains($"Idempotency-Key: {frozen[index].DeliveryId:D}", actual[index].Header, StringComparison.OrdinalIgnoreCase);
        }
        using var fresh = JsonDocument.Parse(actual[^1].Body);
        Assert.Equal(1, fresh.RootElement.GetProperty("values").GetProperty("value").GetInt32());
        Assert.False(fresh.RootElement.GetProperty("values").TryGetProperty("final", out _));
        await OutputPipelineTests.UntilAsync(() => active.Deliveries.GetStatus().DeletedRecords == 3);
        Assert.Equal(1, active.Deliveries.GetStatus().CommittedRecords);
        var store = missing.Services.GetRequiredService<RuntimeVersionStore>();
        Assert.Equal(hash, store.Load(1)!.ContentHash);
        Assert.False(store.ReviewDraft(version, Token).PreflightPassed); // New application still requires the module.
        var restored = RuntimeSnapshotDocument.ReadForExecution(store.Load(1)!);
        var plan = restored.Plan.ToPlan(1);
        Assert.Equal(RuntimeUnitId.Create("output.http", "extended/out"), plan.DiagnosticObjects[broken.Id]);
        Assert.Equal(version, restored.Draft!.Version);
        Assert.Null(store.Load(2));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingTransformKeepsHealthyHttpPathRunningAndNeverReformatsFrozenDeliveries(bool sharedTarget)
    {
        using var data = TemporaryDirectory.Create();
        using var seeds = TemporaryDirectory.Create();
        using var affected = Listen();
        using var separate = Listen();
        var healthy = sharedTarget ? affected : separate;
        var healthyId = sharedTarget ? 1 : 2;
        var seed = Path.Combine(seeds.Path, "seed.json");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            units = new object[]
            {
                new { kind = "input.test", key = "meter/sim", configuration = new { } },
                new { kind = "output.http", key = "a-transformed", configuration = new
                {
                    connectionId = 1, baseAddress = Base(affected), path = "transformed", inputs = TransformInputs,
                    transformStages = new[] { TransformRuntimeTests.Stage(1) },
                } },
                new { kind = "output.http", key = "b-healthy", configuration = new
                { connectionId = healthyId, baseAddress = Base(healthy), path = "healthy", inputs = TransformInputs } },
            },
        });
        await File.WriteAllBytesAsync(seed, bytes, Token);
        var settings = new Dictionary<string, string>
        {
            ["TenonEdge:Outbound:LocalPorts:0"] = ((IPEndPoint)affected.LocalEndpoint).Port.ToString(CultureInfo.InvariantCulture),
            ["TenonEdge:Outbound:LocalPorts:1"] = ((IPEndPoint)separate.LocalEndpoint).Port.ToString(CultureInfo.InvariantCulture),
        };
        var firstRequest = ServeOnceAsync(affected, 401);
        var healthyRequest = sharedTarget ? null : ServeOnceAsync(healthy, 401);
        StoredDelivery[] original;
        string snapshotHash;
        await using (var host = new FixedDataDirectoryApplicationFactory(data.Path,
            new Dictionary<string, string>(settings) { [RuntimeSettings.SeedSnapshotKey] = seed }, services =>
            {
                services.AddSingleton<IInputUnitFactory>(new OneShotInputFactory());
                services.AddSingleton<IMessageTransformFactory>(new TestOffsetTransformFactory());
            }))
        {
            var runtime = host.Services.GetRequiredService<RuntimeHost>();
            var first = await firstRequest.WaitAsync(TimeSpan.FromSeconds(10), Token);
            Assert.Contains("POST /api/transformed ", first.Header, StringComparison.Ordinal);
            AssertValue(first.Body, 43);
            if (healthyRequest is not null)
            { AssertValue((await healthyRequest.WaitAsync(TimeSpan.FromSeconds(10), Token)).Body, 42); }
            await OutputPipelineTests.UntilAsync(() => runtime.Deliveries.GetStatus().CommittedRecords == 2);
            original = [.. runtime.Deliveries.ReadPending(1, 0, 10), .. sharedTarget ? [] : runtime.Deliveries.ReadPending(2, 0, 10)];
            Assert.Equal(2, original.Length);
            Assert.Equal(original[0].Payload.ToArray(), first.Body);
            snapshotHash = host.Services.GetRequiredService<RuntimeVersionStore>().Load(1)!.ContentHash;
        }

        var nextInput = new OneShotInputFactory();
        var expectedFrozen = original.Where(record => record.ConnectionId == healthyId).ToArray();
        var received = ReceiveAsync(healthy, expectedFrozen.Length + 1);
        await using (var host = new FixedDataDirectoryApplicationFactory(data.Path, settings,
            services => services.AddSingleton<IInputUnitFactory>(nextInput)))
        {
            var runtime = host.Services.GetRequiredService<RuntimeHost>();
            Assert.Equal(RuntimeCoordinatorState.Active, host.Services.GetRequiredService<RuntimeCoordinator>().Status.State);
            var failed = Assert.Single(runtime.GetStatus().Units, unit => unit.Id == RuntimeUnitId.Create("output.http", "a-transformed"));
            Assert.Equal(RuntimeUnitState.BuildFailed, failed.State);
            var actual = await received.WaitAsync(TimeSpan.FromSeconds(10), Token);
            for (var index = 0; index < expectedFrozen.Length; index++)
            { AssertFrozen(expectedFrozen[index], actual[index]); }
            var fresh = actual[^1];
            Assert.Contains("POST /api/healthy ", fresh.Header, StringComparison.Ordinal);
            AssertValue(fresh.Body, 42);
            Assert.DoesNotContain(original, record => fresh.Header.Contains($"Idempotency-Key: {record.DeliveryId:D}", StringComparison.OrdinalIgnoreCase));
            await OutputPipelineTests.UntilAsync(() => runtime.Deliveries.GetStatus().DeletedRecords == expectedFrozen.Length + 1);
            // Exactly one new healthy path: no fallback sending the failed path without its transform.
            Assert.Equal(1, runtime.Deliveries.GetStatus().CommittedRecords);
            Assert.Equal(1, nextInput.Started);
            if (!sharedTarget)
            {
                Assert.False(affected.Pending());
                var retained = Assert.Single(runtime.Deliveries.ReadPending(1, 0, 10));
                Assert.Equal(original[0].DeliveryId, retained.DeliveryId);
                Assert.Equal(original[0].Payload.ToArray(), retained.Payload.ToArray());
                Assert.Equal(DeliveryState.Pending, retained.State);
            }
            Assert.Equal(snapshotHash, host.Services.GetRequiredService<RuntimeVersionStore>().Load(1)!.ContentHash);
        }

        // A separate failed target waits for its module to return. A healthy path sharing
        // the target already replayed both frozen paths through the original single sender.
        var restored = new TestOffsetTransformFactory();
        var finalRequest = sharedTarget ? null : ServeOnceAsync(affected, 204);
        await using (var host = new FixedDataDirectoryApplicationFactory(data.Path, settings, services =>
        {
            services.AddSingleton<IInputUnitFactory>(new OneShotInputFactory(0));
            services.AddSingleton<IMessageTransformFactory>(restored);
        }))
        {
            var runtime = host.Services.GetRequiredService<RuntimeHost>();
            Assert.Equal(RuntimeCoordinatorState.Active, host.Services.GetRequiredService<RuntimeCoordinator>().Status.State);
            if (finalRequest is not null)
            { AssertFrozen(original[0], await finalRequest.WaitAsync(TimeSpan.FromSeconds(10), Token)); }
            await OutputPipelineTests.UntilAsync(() => runtime.Deliveries.ReadPending(1, 0, 10).Count == 0 && runtime.Deliveries.ReadPending(2, 0, 10).Count == 0);
            Assert.Equal(0, restored.Calls);
            Assert.Equal(0, runtime.Deliveries.GetStatus().CommittedRecords);
            Assert.Equal(snapshotHash, host.Services.GetRequiredService<RuntimeVersionStore>().Load(1)!.ContentHash);
            Assert.Single(new RuntimeStateReader(data.Path).Snapshots());
        }

        static async Task<(string Header, byte[] Body)[]> ReceiveAsync(TcpListener listener, int count)
        {
            var result = new List<(string Header, byte[] Body)>();
            for (var index = 0; index < count; index++)
            { result.Add(await ServeOnceAsync(listener, 204)); }
            return [.. result];
        }
        static void AssertFrozen(StoredDelivery frozen, (string Header, byte[] Body) actual)
        {
            Assert.Equal(frozen.Payload.ToArray(), actual.Body);
            Assert.Contains($"Idempotency-Key: {frozen.DeliveryId:D}", actual.Header, StringComparison.OrdinalIgnoreCase);
            // TargetAddress freezes the relative path; this fixture's base is /api/.
            Assert.Contains($"POST /api/{frozen.TargetAddress} ", actual.Header, StringComparison.Ordinal);
        }
        static void AssertValue(byte[] body, long expected)
        {
            using var json = JsonDocument.Parse(body);
            Assert.Equal(expected, json.RootElement.GetProperty("values").GetProperty("value").GetInt64());
        }
    }
}
