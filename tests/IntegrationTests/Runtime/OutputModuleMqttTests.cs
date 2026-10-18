using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MQTTnet;
using MQTTnet.Protocol;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Plans;
using TenonEdge.Adapters.Mqtt;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Credentials;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Storage;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.IntegrationTests.Probes.Mqtt;
using TenonEdge.Runtime.Lifecycle;

namespace TenonEdge.IntegrationTests.Runtime;

public sealed class OutputModuleMqttTests(MosquittoBrokerFixture fixture) : IClassFixture<MosquittoBrokerFixture>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Category", "RealBroker")]
    public async Task ModuleAndBuiltinOutputsShareOriginalInputSessionAndPersistentConfirmationAcrossRestart()
    {
        var broker = fixture.Require();
        var topic = MqttProbe.NewTopic("output-module");
        using var data = TemporaryDirectory.Create();
        var (peer, _) = await MqttProbe.ConnectAsync(MqttProbe.Options(broker, "output-peer-" + Guid.NewGuid().ToString("N")), Token);
        using (peer)
        {
            var received = ReceivedMessages.Attach(peer);
            await MqttProbe.SubscribeAsync(peer, topic + "/out/#", Token);
            long id;
            string hash;
            await using (var setup = Host())
            {
                var services = setup.Services;
                var connection = services.GetRequiredService<ConnectionDraftStore>().Write(null,
                    new("0", "shared", "Shared", ConnectionKind.Mqtt, Mqtt: new("127.0.0.1", broker.Port)), "test", null);
                id = connection.Connection.Id;
                await services.GetRequiredService<MqttCredentialStore>().WriteAsync(id,
                    new($"mqtt://127.0.0.1:{broker.Port}", MosquittoBroker.UserName, MosquittoBroker.Password, ExpectedDraftVersion: connection.Version), "test", null, true, Token);
                var module = new TestProtocolOutputModule("mqtt");
                var output = module.Unit(JsonSerializer.SerializeToElement(new { host = "127.0.0.1", port = broker.Port, usePassword = true }), topic + "/out/module", id);
                var input = new RuntimeUnitPlan(RuntimeUnitId.Create("input.mqtt", "meter/source"), JsonSerializer.SerializeToElement(new
                {
                    connectionId = id,
                    host = "127.0.0.1",
                    port = broker.Port,
                    usePassword = true,
                    topicFilter = topic + "/in",
                    qos = 1,
                    fields = new[] { new { key = "value", path = "/value", type = "integer" } }
                }));
                var settings = TenonEdge.Runtime.Outputs.OutputModuleCatalog.ReadProtocol(output)!.Configuration.Deserialize<MqttOutputSettings>(TestProtocolInputModule.Json)!;
                var native = new RuntimeUnitPlan(RuntimeUnitId.Create("output.mqtt", "route/native"),
                    JsonSerializer.SerializeToElement(settings with { Topic = topic + "/out/native" }, TestProtocolInputModule.Json));
                var plan = RuntimePlanDocument.Parse(TestProtocolOutputModule.Document(input, output, native), outputs: services.GetRequiredService<RuntimeHost>().OutputModules);
                var versions = services.GetRequiredService<RuntimeVersionStore>();
                versions.Commit(versions.Freeze(plan, RuntimeSnapshotOrigins.Seed, null, "test"), null);
                hash = versions.Load(1)!.ContentHash;
                await using var prepared = services.GetRequiredService<RuntimeHost>().Prepare(plan.ToPlan(1));
                Assert.Empty(prepared.Failures);
                Assert.Equal(2, prepared.Outputs.Count);
                Assert.Same(prepared.Outputs[0].State, prepared.Outputs[1].State);
                Assert.All(prepared.Outputs, target =>
                {
                    Assert.True(target.Target.QuiesceWithInputs);
                    Assert.Equal("output.mqtt", target.ProtocolKind);
                    Assert.NotNull(target.RecoveryGroup);
                });
                Assert.Equal("mqtt", prepared.CaptureMigrationTarget(id)!.Recipient.Protocol);
            }
            for (var phase = 0; phase < 2; phase++)
            {
                await using var host = Host();
                var runtime = host.Services.GetRequiredService<RuntimeHost>();
                await OutputPipelineTests.UntilAsync(() => runtime.Deliveries.GetPersistentCounters().GetValueOrDefault($"connection/{id}/mqtt/subscribed") == phase + 1);
                Assert.Equal(hash, host.Services.GetRequiredService<RuntimeVersionStore>().Load(1)!.ContentHash);
                Assert.NotNull(runtime.CaptureMigrationTarget(id, 1));
                await MqttProbe.PublishAsync(peer, topic + "/in", "{\"value\":9007199254740993}"u8.ToArray(), MqttQualityOfServiceLevel.AtLeastOnce, Token);
                var topics = new HashSet<string>();
                var ids = new HashSet<string>();
                var sources = new HashSet<string>();
                for (var index = 0; index < 2; index++)
                {
                    var receivedMessage = await received.NextAsync(Token);
                    topics.Add(receivedMessage.ApplicationMessage.Topic);
                    using var body = JsonDocument.Parse(receivedMessage.ApplicationMessage.ConvertPayloadToString());
                    Assert.Equal("9007199254740993", body.RootElement.GetProperty("values").GetProperty("value").GetRawText());
                    ids.Add(body.RootElement.GetProperty("messageId").GetString()!);
                    sources.Add(body.RootElement.GetProperty("sourceMessageId").GetString()!);
                }
                Assert.Equal(2, topics.Count);
                Assert.Equal(2, ids.Count);
                Assert.Single(sources);
                await OutputPipelineTests.UntilAsync(() => runtime.Deliveries.GetStatus().DeletedRecords == 2 &&
                    runtime.Deliveries.GetPersistentCounters().GetValueOrDefault($"connection/{id}/mqtt/acknowledged") == phase + 1);
                Assert.All(runtime.GetStatus().Units, unit => Assert.Equal(RuntimeUnitState.Running, unit.State));
            }
        }
        FixedDataDirectoryApplicationFactory Host() => new(data.Path, new Dictionary<string, string>
        { ["TenonEdge:Outbound:LocalPorts:0"] = broker.Port.ToString(CultureInfo.InvariantCulture) },
            services => services.AddSingleton<IOutputModuleFactory>(new TestProtocolOutputModule("mqtt")));
    }
}
