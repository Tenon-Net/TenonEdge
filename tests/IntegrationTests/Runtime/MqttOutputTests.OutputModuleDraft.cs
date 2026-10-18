using System.Buffers;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Host.Features.Credentials;
using TenonEdge.Host.Features.Deliveries;
using TenonEdge.Host.Features.Routes;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Security;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.IntegrationTests.Probes.Mqtt;
using TenonEdge.IntegrationTests.Routes;
using TenonEdge.Runtime.Deliveries;
using TenonEdge.Runtime.Lifecycle;

namespace TenonEdge.IntegrationTests.Runtime;

public sealed partial class MqttOutputTests
{
    [Fact]
    public async Task OutputModuleGraphPreviewsSendsOnceAppliesAndRestartsThroughOriginalMqttSender()
    {
        var broker = fixture.Require();
        var topic = MqttProbe.NewTopic("module-draft");
        var (subscriber, _) = await MqttProbe.ConnectAsync(MqttProbe.Options(broker, NewClientId()), Token);
        using (subscriber)
        using (var data = TemporaryDirectory.Create())
        {
            var received = ReceivedMessages.Attach(subscriber);
            await MqttProbe.SubscribeAsync(subscriber, topic, Token);
            var settings = new Dictionary<string, string> { ["TenonEdge:Outbound:LocalPorts:0"] = broker.Port.ToString(CultureInfo.InvariantCulture) };
            StoredDelivery frozen;
            string hash;
            await using (var host = new FixedDataDirectoryApplicationFactory(data.Path, settings,
                services => services.AddSingleton<IOutputModuleFactory>(new TestProtocolOutputModule("mqtt"))))
            {
                var seed = RouteOutputModuleTests.Seed(host.Services, "mqtt", broker.Port);
                var node = seed.Graph.Nodes[^1];
                var config = node.Config.Deserialize<RouteModuleOutputDraft>(RouteGraphFormat.Json)!;
                var target = config.ConnectionId!.Value;
                config = config with { Parameters = JsonSerializer.SerializeToElement(new { address = topic }) };
                var graph = seed.Graph with { Nodes = [.. seed.Graph.Nodes[..^1], node with { Config = JsonSerializer.SerializeToElement(config, RouteGraphFormat.Json) }] };
                await host.Services.GetRequiredService<MqttCredentialStore>().WriteAsync(target,
                    new($"mqtt://127.0.0.1:{broker.Port}", MosquittoBroker.UserName, MosquittoBroker.Password, ExpectedDraftVersion: seed.ExpectedVersion),
                    "fixture", null, false, token: Token);
                TestAccounts.Create(host.Services, "editor", ApiRequests.StrongPassword, UserRole.Configurator);
                using var client = await host.CreateLoggedInClientAsync("editor", Token);
                var (output, confirmation) = await RouteOutputModuleTests.PreviewAndPrepare(client, seed with { Graph = graph });
                Assert.Empty(await received.CollectAsync(TimeSpan.FromMilliseconds(100), Token));
                using var sent = await client.PostJsonAsync("/api/edge-admin/v1/routes/test-send/confirm", confirmation, Token);
                Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
                Assert.Equal("mqtt.puback", (await sent.ReadJsonAsync(Token)).GetProperty("code").GetString());
                var testWire = await received.NextAsync(Token);
                Assert.Equal(Encoding.UTF8.GetBytes(output.GetProperty("body").GetString()!), testWire.ApplicationMessage.Payload.ToArray());
                using var duplicate = await client.PostJsonAsync("/api/edge-admin/v1/routes/test-send/confirm", confirmation, Token);
                Assert.Equal(HttpStatusCode.NotFound, duplicate.StatusCode);
                Assert.Empty(await received.CollectAsync(TimeSpan.FromMilliseconds(100), Token));
                var cache = host.Services.GetRequiredService<DeliveryCache>();
                Assert.Equal(0, cache.GetStatus().CommittedRecords);
                host.Services.GetRequiredService<OutputControlStore>().Write(target, new(true, true), "fixture", null);
                var saved = host.Services.GetRequiredService<RouteDraftStore>().Write(null,
                    new(seed.ExpectedVersion, "module_route", "Module", true, graph), "fixture", null);
                var applied = await host.Services.GetRequiredService<RuntimeCoordinator>().ApplyDraftAsync(saved.Version, "fixture", null, Token);
                Assert.True(applied.Activated);
                await OutputPipelineTests.UntilAsync(() => cache.GetStatus().CommittedRecords == 1);
                frozen = Assert.Single(cache.ReadPending(target, 0, 10));
                Assert.Contains("\"scaled\":9007199254740994", Encoding.UTF8.GetString(frozen.Payload.Span), StringComparison.Ordinal);
                Assert.Equal(topic, frozen.TargetAddress);
                Assert.Empty(await received.CollectAsync(TimeSpan.FromMilliseconds(100), Token));
                hash = host.Services.GetRequiredService<RuntimeVersionStore>().Load(1)!.ContentHash;
            }
            await using (var missing = new FixedDataDirectoryApplicationFactory(data.Path, settings))
            {
                var runtime = missing.Services.GetRequiredService<RuntimeHost>();
                Assert.Equal(RuntimeCoordinatorState.Active, missing.Services.GetRequiredService<RuntimeCoordinator>().Status.State);
                Assert.Equal(RuntimeUnitState.BuildFailed, Assert.Single(runtime.GetStatus().Units, unit => unit.Id.Kind == "output.testmqtt").State);
                await OutputPipelineTests.UntilAsync(() => runtime.GetStatus().Units.Any(unit => unit.Id.Kind == "input.simulator" && unit.Reads?.ReadsCompleted >= 1));
                Assert.Equal(frozen.Payload.ToArray(), Assert.Single(runtime.Deliveries.ReadPending(frozen.ConnectionId, 0, 10)).Payload.ToArray());
                Assert.Empty(await received.CollectAsync(TimeSpan.FromMilliseconds(100), Token));
                Assert.Equal(hash, missing.Services.GetRequiredService<RuntimeVersionStore>().Load(1)!.ContentHash);
            }
            await using var restored = new FixedDataDirectoryApplicationFactory(data.Path, settings,
                services => services.AddSingleton<IOutputModuleFactory>(new TestProtocolOutputModule("mqtt")));
            var restoredCache = restored.Services.GetRequiredService<DeliveryCache>();
            await OutputPipelineTests.UntilAsync(() => restoredCache.ReadPending(frozen.ConnectionId, 0, 10).Count == 2);
            var controls = restored.Services.GetRequiredService<OutputControlStore>();
            var pause = controls.Read(frozen.ConnectionId);
            Assert.True(pause.ManuallyPaused);
            Assert.Empty(await received.CollectAsync(TimeSpan.FromMilliseconds(100), Token));
            controls.Write(frozen.ConnectionId, new(false, true, pause.ResumeVersion), "fixture", null);
            var replay = await received.NextAsync(Token);
            Assert.Equal(frozen.Payload.ToArray(), replay.ApplicationMessage.Payload.ToArray());
            Assert.Equal(frozen.TargetAddress, replay.ApplicationMessage.Topic);
            var fresh = await received.NextAsync(Token);
            using var newBody = JsonDocument.Parse(fresh.ApplicationMessage.Payload.ToArray());
            Assert.Equal(9007199254740994L, newBody.RootElement.GetProperty("values").GetProperty("scaled").GetInt64());
            Assert.NotEqual(frozen.DeliveryId, newBody.RootElement.GetProperty("messageId").GetGuid());
            await OutputPipelineTests.UntilAsync(() => restoredCache.GetStatus().DeletedRecords == 2);
            Assert.Empty(restoredCache.ReadPending(frozen.ConnectionId, 0, 10));
            Assert.Equal(hash, restored.Services.GetRequiredService<RuntimeVersionStore>().Load(1)!.ContentHash);
        }
    }
}
