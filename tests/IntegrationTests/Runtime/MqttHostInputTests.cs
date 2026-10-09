using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MQTTnet;
using MQTTnet.Protocol;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.IntegrationTests.Probes.Mqtt;
using TenonEdge.Runtime.Deliveries;

namespace TenonEdge.IntegrationTests.Runtime;

[Trait("Category", "RealBroker")]
public sealed class MqttHostInputTests(MosquittoBrokerFixture fixture) : IClassFixture<MosquittoBrokerFixture>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly string[] Inputs = ["device/mqtt"];

    [Fact]
    public async Task ProductHostUsesSavedCredentialsToReceiveAndForwardAndCountsIgnoredRetainedMessages()
    {
        var broker = fixture.Require();
        using var data = TemporaryDirectory.Create();
        using var seeds = TemporaryDirectory.Create();
        await using (var bootstrap = new FixedDataDirectoryApplicationFactory(data.Path))
        {
            using var api = bootstrap.CreateClient();
            using var setup = await api.CompleteSetupAsync(Token);
            await api.UseSessionAsync(setup, Token);
            await OutputPipelineTests.UntilAsync(() => bootstrap.Services.GetRequiredService<DeliveryCache>().GetStatus().Store is not null);
            using var saved = await api.PutAsJsonAsync(new Uri("/api/edge-admin/v1/connections/1/mqtt-credential", UriKind.Relative),
                new { recipient = $"mqtt://127.0.0.1:{broker.Port}", username = MosquittoBroker.UserName, password = MosquittoBroker.Password }, Token);
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        }
        var topic = MqttProbe.NewTopic("product-host-input");
        var seed = Path.Combine(seeds.Path, "mqtt.json");
        await File.WriteAllTextAsync(seed, JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            units = new object[]
            {
                new { kind = "input.mqtt", key = "device/mqtt", configuration = new
                {
                    connectionId = 1, host = "127.0.0.1", port = broker.Port, usePassword = true, topicFilter = topic + "/in",
                    fields = new[] { new { key = "value", path = "/v", type = "integer" } },
                } },
                new { kind = "output.mqtt", key = "out", configuration = new
                { connectionId = 1, host = "127.0.0.1", port = broker.Port, usePassword = true, topic = topic + "/out", inputs = Inputs } },
            },
        }), Token);
        var (peer, _) = await MqttProbe.ConnectAsync(MqttProbe.Options(broker, "host-peer-" + Guid.NewGuid().ToString("N")), Token);
        using (peer)
        {
            var received = ReceivedMessages.Attach(peer);
            await MqttProbe.SubscribeAsync(peer, topic + "/out", Token);
            await peer.PublishAsync(new MqttApplicationMessageBuilder().WithTopic(topic + "/in").WithPayload("{\"v\":7}")
                .WithRetainFlag().WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).Build(), Token);
            // 完整释放并重建产品 Host：即使 Broker 持久会话存在也重订；默认忽略每次回放的保留消息。
            for (var restart = 0; restart < 2; restart++)
            {
                await using var host = new FixedDataDirectoryApplicationFactory(data.Path, new Dictionary<string, string>
                {
                    [RuntimeSettings.SeedSnapshotKey] = seed,
                    ["TenonEdge:Outbound:LocalPorts:0"] = broker.Port.ToString(CultureInfo.InvariantCulture),
                });
                using var started = host.CreateClient();
                Assert.Single(host.Services.GetServices<IRuntimePlanAdapterProvider>());
                Assert.DoesNotContain(host.Services.GetServices<IOutputAdapterFactory>(), factory => factory.Kind == "output.mqtt");
                var runtime = host.Services.GetRequiredService<TenonEdge.Runtime.Lifecycle.RuntimeHost>();
                Assert.Equal(8L * 1024 * 1024, runtime.InFlight.BufferCapacity);
                Assert.Equal(64L * 1024 * 1024, runtime.InFlight.Status.CapacityBytes);
                var cache = host.Services.GetRequiredService<DeliveryCache>();
                await OutputPipelineTests.UntilAsync(() => cache.GetPersistentCounters().GetValueOrDefault("connection/1/mqtt/retainedIgnored") == 1 + restart);
                await MqttProbe.PublishAsync(peer, topic + "/in", "{\"v\":9223372036854775807}"u8.ToArray(), MqttQualityOfServiceLevel.AtLeastOnce, Token);
                var forwarded = await received.NextAsync(Token);
                using var payload = JsonDocument.Parse(forwarded.ApplicationMessage.ConvertPayloadToString());
                Assert.Equal(long.MaxValue, payload.RootElement.GetProperty("values").GetProperty("value").GetInt64());
                await OutputPipelineTests.UntilAsync(() => cache.GetStatus().DeletedRecords == 1 &&
                    cache.GetPersistentCounters().GetValueOrDefault("connection/1/mqtt/acknowledged") == 2 * (restart + 1));
                Assert.Equal(1 + restart, cache.GetPersistentCounters()["connection/1/mqtt/subscribed"]);
                using var admin = await host.CreateLoggedInClientAsync("admin", Token);
                var status = await admin.GetJsonAsync("/api/edge-admin/v1/runtime/status", Token);
                Assert.Contains("input.mqtt", status.GetRawText(), StringComparison.Ordinal);
                Assert.Equal(1 + restart, status.GetProperty("persistentCounters").GetProperty("connection/1/mqtt/retainedIgnored").GetInt64());
                Assert.DoesNotContain(MosquittoBroker.Password, status.GetRawText(), StringComparison.Ordinal);
            }
        }
    }
}
