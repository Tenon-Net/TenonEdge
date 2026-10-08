using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using MQTTnet;
using MQTTnet.Protocol;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.IntegrationTests.Probes.Mqtt;
using TenonEdge.Runtime.Deliveries;

namespace TenonEdge.IntegrationTests.Runtime;

[Trait("Category", "RealBroker")]
public sealed class LoraMqttSampleTests(MosquittoBrokerFixture fixture) : IClassFixture<MosquittoBrokerFixture>
{
    [Fact]
    public async Task PublishedSamplesUseRealHostAndBrokerWithoutCrossDeviceDataOrFabricatedDecodedValues()
    {
        var token = TestContext.Current.CancellationToken;
        var broker = fixture.Require();
        using var data = TemporaryDirectory.Create();
        using var seeds = TemporaryDirectory.Create();
        await using (var bootstrap = new FixedDataDirectoryApplicationFactory(data.Path))
        {
            using var api = bootstrap.CreateClient();
            using var setup = await api.CompleteSetupAsync(token);
            await api.UseSessionAsync(setup, token);
            using var credential = await api.PutAsJsonAsync(new Uri("/api/edge-admin/v1/connections/1/mqtt-credential", UriKind.Relative),
                new { recipient = $"mqtt://127.0.0.1:{broker.Port}", username = MosquittoBroker.UserName, password = MosquittoBroker.Password }, token);
            Assert.Equal(HttpStatusCode.OK, credential.StatusCode);
        }
        var prefix = MqttProbe.NewTopic("lora-sample");
        var seed = JsonNode.Parse(await File.ReadAllTextAsync(Sample("runtime-seed.json"), token))!;
        var inputs = new List<string>();
        foreach (var unit in seed["units"]!.AsArray())
        {
            var configuration = unit!["configuration"]!;
            configuration["port"] = broker.Port;
            if (configuration["topicFilter"] is { } topic)
            {
                var isolated = prefix + "/" + topic.GetValue<string>();
                configuration["topicFilter"] = isolated;
                inputs.Add(isolated);
            }
            if (configuration["topic"] is { } output)
            { configuration["topic"] = prefix + "/" + output.GetValue<string>(); }
        }
        var seedFile = Path.Combine(seeds.Path, "lora.json");
        var seedJson = seed.ToJsonString();
        _ = RuntimePlanDocument.Parse(System.Text.Encoding.UTF8.GetBytes(seedJson));
        await File.WriteAllTextAsync(seedFile, seedJson, token);
        var (peer, _) = await MqttProbe.ConnectAsync(MqttProbe.Options(broker, "lora-observer-" + Guid.NewGuid().ToString("N")), token);
        using (peer)
        {
            var received = ReceivedMessages.Attach(peer);
            await MqttProbe.SubscribeAsync(peer, prefix + "/tenon/lora/+", token);
            await using var host = new FixedDataDirectoryApplicationFactory(data.Path, new Dictionary<string, string>
            {
                [RuntimeSettings.SeedSnapshotKey] = seedFile,
                ["TenonEdge:Outbound:LocalPorts:0"] = broker.Port.ToString(CultureInfo.InvariantCulture),
            });
            using var started = host.CreateClient();
            var coordinator = host.Services.GetRequiredService<RuntimeCoordinator>();
            Assert.True(coordinator.Status.State == RuntimeCoordinatorState.Active, JsonSerializer.Serialize(coordinator.Status));
            var cache = host.Services.GetRequiredService<DeliveryCache>();
            await OutputPipelineTests.UntilAsync(() => cache.GetPersistentCounters().GetValueOrDefault("connection/1/mqtt/subscribed") == 2);
            // 两设备交替发布，后一次相同样本是顺序屏障，不依赖“睡一会没收到”的断言。
            await PublishAsync(0, "soil-001.json");
            await AssertNextAsync("soil-001", 23.7m, 41.5m, 92);
            await PublishAsync(1, "soil-002.json");
            await AssertNextAsync("soil-002", 18.2m, 55.5m, 81);
            await PublishAsync(0, "missing-decoded-object.json");
            await PublishAsync(0, "invalid-temperature.json");
            await OutputPipelineTests.UntilAsync(() => cache.GetPersistentCounters().GetValueOrDefault("path/soil-001/up/1/dataRejected") == 2);
            await PublishAsync(1, "soil-002.json");
            await AssertNextAsync("soil-002", 18.2m, 55.5m, 81);
            await PublishAsync(0, "soil-001.json");
            await AssertNextAsync("soil-001", 23.7m, 41.5m, 92);
            await OutputPipelineTests.UntilAsync(() => cache.GetStatus().DeletedRecords == 4 &&
                cache.GetPersistentCounters().GetValueOrDefault("connection/1/mqtt/acknowledged") == 6);
            Assert.Equal(4, cache.GetStatus().CommittedRecords);
            var counters = cache.GetPersistentCounters();
            Assert.Equal(2, counters["path/soil-001/up/1/requiredFieldFailures"]);
            Assert.Equal(3, counters["path/soil-001/up/1/mappingMissingFields"]);
            Assert.Equal(1, counters["path/soil-001/up/1/mappingInvalidFields"]);
            Assert.Equal(1, counters["input/soil-001/up/failedFields"]);
            using var admin = await host.CreateLoggedInClientAsync("admin", token);
            var status = await admin.GetJsonAsync("/api/edge-admin/v1/runtime/status", token);
            Assert.Equal(2, status.GetProperty("persistentCounters").GetProperty("path/soil-001/up/1/dataRejected").GetInt64());

            async Task PublishAsync(int device, string sample) => await MqttProbe.PublishAsync(peer, inputs[device],
                await File.ReadAllBytesAsync(Sample(sample), token), MqttQualityOfServiceLevel.AtLeastOnce, token);
            async Task AssertNextAsync(string device, decimal temperature, decimal moisture, int battery)
            {
                var message = (await received.NextAsync(token)).ApplicationMessage;
                Assert.Equal(prefix + "/tenon/lora/" + device, message.Topic);
                using var document = JsonDocument.Parse(message.ConvertPayloadToString());
                var root = document.RootElement;
                Assert.Equal(device, root.GetProperty("deviceKey").GetString());
                Assert.Equal("upstream", root.GetProperty("timeSource").GetString());
                Assert.Equal("2026-01-01T00:00:00.000Z", root.GetProperty("collectedAt").GetString());
                var values = root.GetProperty("values");
                Assert.Equal(3, values.EnumerateObject().Count());
                Assert.Equal(temperature, values.GetProperty("temperature").GetDecimal());
                Assert.Equal(moisture, values.GetProperty("soilMoisture").GetDecimal());
                Assert.Equal(battery, values.GetProperty("batteryPercent").GetInt32());
                Assert.Empty(root.GetProperty("quality").EnumerateObject());
            }
        }
    }

    private static string Sample(string name) => Path.Combine(AppContext.BaseDirectory, "Samples", "Lora", name);
}
