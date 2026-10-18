using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Host.Features.Configuration;
using TenonEdge.Host.Features.Configuration.Transfer;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Devices;
using TenonEdge.Host.Features.Inputs;
using TenonEdge.Host.Features.Routes;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Security;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.Runtime.Lifecycle;

namespace TenonEdge.IntegrationTests.Runtime;

public sealed partial class HttpOutputTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConnectionModuleDraftImportsRemapsAppliesSharedTcpAndRestartsWithFrozenHttp(bool replace)
    {
        using var sourceData = TemporaryDirectory.Create();
        using var targetData = TemporaryDirectory.Create();
        using var source = Listen();
        using var sink = Listen();
        byte[] portable;
        long originalId;
        await using (var host = new FixedDataDirectoryApplicationFactory(sourceData.Path,
            configureServices: services => Register(services, true, true, _ => { })))
        {
            TestAccounts.Create(host.Services, "operator", ApiRequests.StrongPassword, UserRole.Configurator);
            using var client = await host.CreateLoggedInClientAsync("operator", Token);
            var request = new ConnectionDraftRequest("0", "wire", "Wire", ConnectionKind.Module,
                Module: new("testwire", 1, JsonSerializer.SerializeToElement(new { host = "127.0.0.1", port = ((IPEndPoint)source.LocalEndpoint).Port })));
            using var response = await client.PostJsonAsync("/api/edge-admin/v1/connections", request, Token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var connection = (await response.Content.ReadFromJsonAsync<ConnectionDraftResult>(RouteSourceCompiler.Json, Token))!;
            originalId = connection.Connection.Id;
            var connections = host.Services.GetRequiredService<ConnectionDraftStore>();
            var target = connections.Write(null, new(connection.Version, "sink", "Sink", ConnectionKind.Http, Http: new(Base(sink))), "fixture", null);
            var version = target.Version;
            var sources = new List<long>();
            foreach (var key in new[] { "meter", "second" })
            {
                var device = host.Services.GetRequiredService<DeviceDraftStore>().Write(null,
                    new(version, key, key, DeviceKind.Module, ModuleKind: "input.testwire"), "fixture", null);
                var input = host.Services.GetRequiredService<InputDraftStore>().Write(device.Device.Id, null,
                    new(device.Version, "read", "Read", Module: new("input.testwire", 1, JsonSerializer.SerializeToElement(new { }),
                        new() { ["source"] = originalId })), "fixture", null);
                sources.Add(input.Input.Id);
                version = input.Version;
            }
            Assert.Equal("connection.deviceReferenced", Assert.Throws<DraftWriteException>(() => connections.Delete(originalId, version, "fixture", null)).Code);
            var graph = new RouteGraph(1,
                [new("in", "input.testwire", 1, "Wire", JsonSerializer.SerializeToElement(new { sourceIds = sources })),
                 new("out", "output.http", 1, "HTTP", JsonSerializer.SerializeToElement(new { connectionId = target.Connection.Id, path = "data" }))],
                [new("in", "out")], new([]));
            var saved = host.Services.GetRequiredService<RouteDraftStore>().Write(null, new(version, "wire_route", "Wire route", true, graph), "fixture", null);
            portable = host.Services.GetRequiredService<ConfigurationExportStore>().Export(saved.Version, Token);
            Assert.False(source.Pending()); // Editing and exporting must not connect.
        }
        var file = ConfigurationImportDocument.Read(portable, Token);
        Assert.All(file.Inputs, input => Assert.Equal("wire", input.Module!.Connections!["source"]));
        var settings = new Dictionary<string, string>
        {
            ["TenonEdge:Outbound:LocalPorts:0"] = ((IPEndPoint)source.LocalEndpoint).Port.ToString(CultureInfo.InvariantCulture),
            ["TenonEdge:Outbound:LocalPorts:1"] = ((IPEndPoint)sink.LocalEndpoint).Port.ToString(CultureInfo.InvariantCulture),
        };
        var wire = ServeWireAsync(source);
        var rejected = ServeOnceAsync(sink, 401);
        Dictionary<Guid, byte[]> frozen;
        long sourceId, sinkId;
        string hash;
        byte[] exported;
        TestWireModule? module = null;
        await using (var host = new FixedDataDirectoryApplicationFactory(targetData.Path, settings,
            services => Register(services, true, true, value => module = value)))
        {
            TestAccounts.Create(host.Services, "operator", ApiRequests.StrongPassword, UserRole.Configurator);
            var actor = TestAccounts.Find(host.Services, "operator")!;
            var imports = host.Services.GetRequiredService<ConfigurationImportStore>();
            var candidate = imports.Prepare(portable, replace ? ConfigurationImportMode.Replace : ConfigurationImportMode.Merge, "0", Token);
            sourceId = Assert.Single(candidate.Draft.Connections, value => value.Key == "wire").Id;
            sinkId = Assert.Single(candidate.Draft.Connections, value => value.Key == "sink").Id;
            Assert.NotEqual(originalId, sourceId);
            Assert.All(candidate.Draft.Inputs, input => Assert.Equal(sourceId, input.Module!.Connections!["source"]));
            var committed = imports.Commit(candidate, new(actor.Id, actor.SecurityStamp, null), Token);
            var routes = host.Services.GetRequiredService<RouteDraftStore>();
            var snapshot = routes.ReadCompilation(committed.Version, Token);
            var compiled = RoutePlanCompiler.Compile(snapshot, Token);
            Assert.Equal(2, compiled.Document.Units.Count(unit => unit.Id.Kind == "input.testwire"));
            Assert.Single(compiled.Document.Units, unit => unit.Id.Kind == "output.http");
            Assert.True(host.Services.GetRequiredService<RuntimeVersionStore>().ReviewDraft(committed.Version, Token).PreflightPassed);
            Assert.Equal(0, module!.Builds);
            Assert.False(source.Pending());
            exported = host.Services.GetRequiredService<ConfigurationExportStore>().Export(committed.Version, Token);
            var applied = await host.Services.GetRequiredService<RuntimeCoordinator>().ApplyDraftAsync(committed.Version, "operator", null, Token);
            Assert.True(applied.Activated);
            _ = await rejected.WaitAsync(TimeSpan.FromSeconds(15), Token);
            var runtime = host.Services.GetRequiredService<RuntimeHost>();
            await OutputPipelineTests.UntilAsync(() => runtime.Deliveries.ReadPending(sinkId, 0, 3).Count == 2);
            frozen = runtime.Deliveries.ReadPending(sinkId, 0, 3).ToDictionary(value => value.DeliveryId, value => value.Payload.ToArray());
            Assert.All(frozen.Values, payload =>
            {
                using var json = JsonDocument.Parse(payload);
                Assert.Equal("9007199254740993", json.RootElement.GetProperty("values").GetProperty("value").GetRawText());
            });
            Assert.Equal(1, module.Sessions);
            Assert.Equal(2, module.Reads);
            hash = host.Services.GetRequiredService<RuntimeVersionStore>().Load(1)!.ContentHash;
            // Remove only draft users via the original atomic import. The effective
            // checkpoint must still protect its connection after a normal restart.
            var withoutUsers = file with { Devices = [], Inputs = [], Routes = [] };
            var next = imports.Prepare(ConfigurationExport.Serialize(withoutUsers, Token), ConfigurationImportMode.Replace, committed.Version, Token);
            var edited = imports.Commit(next, new(actor.Id, actor.SecurityStamp, null), Token);
            Assert.Equal("connection.referenced", Assert.Throws<DraftWriteException>(() => host.Services.GetRequiredService<ConnectionDraftStore>()
                .Delete(sourceId, edited.Version, "fixture", null)).Code);
            exported = host.Services.GetRequiredService<ConfigurationExportStore>().Export(edited.Version, Token);
        }
        Assert.Equal(2, await wire.WaitAsync(TimeSpan.FromSeconds(10), Token));
        Assert.Equal(1, module!.SessionDisposals);
        async Task<List<(string Header, byte[] Body)>> ReceiveBoth()
        { return [await ServeOnceAsync(sink, 204), await ServeOnceAsync(sink, 204)]; }
        var replay = ReceiveBoth();
        await using var missing = new FixedDataDirectoryApplicationFactory(targetData.Path, settings,
            services => Register(services, false, true, value => module = value));
        // WebApplicationFactory starts lazily; waiting for the receiver alone does
        // not start the restarted Host or its original sender.
        var restartedRuntime = missing.Services.GetRequiredService<RuntimeHost>();
        var received = await replay.WaitAsync(TimeSpan.FromSeconds(15), Token);
        Assert.All(frozen, pair => Assert.Contains(received, value => value.Body.SequenceEqual(pair.Value) &&
            value.Header.Contains(pair.Key.ToString(), StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(2, restartedRuntime.GetStatus().Units.Count(unit => unit.State == RuntimeUnitState.BuildFailed));
        Assert.Equal(0, module!.Builds);
        Assert.False(source.Pending());
        Assert.Equal(hash, missing.Services.GetRequiredService<RuntimeVersionStore>().Load(1)!.ContentHash);
        Assert.Equal(exported, missing.Services.GetRequiredService<ConfigurationExportStore>().Export(null, Token));
        var drafts = missing.Services.GetRequiredService<ConnectionDraftStore>();
        Assert.Equal("connection.referenced", Assert.Throws<DraftWriteException>(() => drafts.Delete(sourceId, drafts.Read().Version, "fixture", null)).Code);
        Assert.Throws<ConfigurationImportException>(() => missing.Services.GetRequiredService<ConfigurationImportStore>()
            .Prepare(portable, ConfigurationImportMode.Replace, drafts.Read().Version, Token));
    }
}
