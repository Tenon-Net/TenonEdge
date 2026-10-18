using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MQTTnet;
using MQTTnet.Protocol;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Adapters.Modbus;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Credentials;
using TenonEdge.Host.Features.Devices;
using TenonEdge.Host.Features.Inputs;
using TenonEdge.Host.Features.Routes;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.IntegrationTests.Probes.Modbus;
using TenonEdge.IntegrationTests.Probes.Mqtt;
using TenonEdge.Runtime.Inputs;
using TenonEdge.Runtime.Lifecycle;

namespace TenonEdge.IntegrationTests.Runtime;

public sealed class InputModuleProtocolTests(MosquittoBrokerFixture fixture) : IClassFixture<MosquittoBrokerFixture>
{
    private static readonly string[] ExpectedDevices = ["builtin", "module"];
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value, RouteSourceCompiler.Json);
    private static FixedDataDirectoryApplicationFactory Host(string path, string protocol, int port) => new(path,
        new Dictionary<string, string> { ["TenonEdge:Outbound:LocalPorts:0"] = port.ToString(CultureInfo.InvariantCulture) },
        services => services.AddSingleton<IInputModuleFactory>(new TestProtocolInputModule(protocol)));

    [Fact]
    [Trait("Category", "RealBroker")]
    public async Task MqttModuleDraftSharesOneOriginalSessionWithBuiltinInputAndOutputAcrossRestart()
    {
        var broker = fixture.Require();
        using var data = TemporaryDirectory.Create();
        var topic = MqttProbe.NewTopic("module-protocol");
        var (peer, _) = await MqttProbe.ConnectAsync(MqttProbe.Options(broker, "module-peer-" + Guid.NewGuid().ToString("N")), Token);
        using (peer)
        {
            var received = ReceivedMessages.Attach(peer);
            await MqttProbe.SubscribeAsync(peer, topic + "/out", Token);
            long connectionId = 0;
            string? hash = null;
            for (var phase = 0; phase < 2; phase++)
            {
                await using var host = Host(data.Path, "mqtt", broker.Port);
                var services = host.Services;
                var runtime = services.GetRequiredService<RuntimeHost>();
                if (phase == 0)
                {
                    var connection = services.GetRequiredService<ConnectionDraftStore>().Write(null,
                        new("0", "shared", "Shared", ConnectionKind.Mqtt, Mqtt: new("127.0.0.1", broker.Port)), "test", null);
                    connectionId = connection.Connection.Id;
                    await services.GetRequiredService<MqttCredentialStore>().WriteAsync(connectionId,
                        new($"mqtt://127.0.0.1:{broker.Port}", MosquittoBroker.UserName, MosquittoBroker.Password, ExpectedDraftVersion: connection.Version), "test", null, true, Token);
                    var device = services.GetRequiredService<DeviceDraftStore>().Write(null,
                        new(connection.Version, "module", "Module", DeviceKind.Module, ModuleKind: "input.testmqtt"), "test", null);
                    var source = services.GetRequiredService<InputDraftStore>().Write(device.Device.Id, null,
                        new(device.Version, "source", "Source", Module: new("input.testmqtt", 1, Json(new { topic = topic + "/module" }), new() { ["connection"] = connectionId })), "test", null);
                    var builtin = services.GetRequiredService<DeviceDraftStore>().Write(null,
                        new(source.Version, "builtin", "Builtin", DeviceKind.Mqtt, ConnectionId: connectionId), "test", null);
                    var input = services.GetRequiredService<InputDraftStore>().Write(builtin.Device.Id, null,
                        new(builtin.Version, "source", "Source", Mqtt: new(topic + "/builtin", Fields: [new("value", "/value", FieldType.Integer)])), "test", null);
                    var graph = new RouteGraph(1,
                        [new("module", "input.testmqtt", 1, "Module", Json(new { sourceIds = new[] { source.Input.Id } })),
                            new("builtin", "input.mqtt", 1, "Builtin", Json(new { sourceIds = new[] { input.Input.Id } })),
                            new("out", "output.mqtt", 1, "Output", Json(new { connectionId, topic = topic + "/out", qos = 1 }))],
                        [new("module", "out"), new("builtin", "out")], new([]));
                    var routes = services.GetRequiredService<RouteDraftStore>();
                    var route = routes.Write(null, new(input.Version, "relay", "Relay", true, graph), "test", null);
                    var compiled = RoutePlanCompiler.Compile(routes.ReadCompilation(route.Version, Token), Token);
                    var frozen = Assert.Single(compiled.Document.Units, unit => unit.Id.Kind == "input.testmqtt");
                    Assert.True(InputModuleCatalog.ReadProtocol(frozen)!.Configuration.GetProperty("usePassword").GetBoolean());
                    Assert.DoesNotContain(MosquittoBroker.Password, compiled.Document.CanonicalText, StringComparison.Ordinal);
                    Assert.True((await services.GetRequiredService<RuntimeCoordinator>().ApplyDraftAsync(route.Version, "test", null, Token)).Activated);
                    hash = services.GetRequiredService<RuntimeVersionStore>().Load(1)!.ContentHash;
                    // Changed draft must not replace the frozen recipient on restart.
                    services.GetRequiredService<ConnectionDraftStore>().Write(connectionId,
                        new(route.Version, "shared", "Unapplied", ConnectionKind.Mqtt, Mqtt: new("unapplied.invalid")), "test", null);
                }
                await OutputPipelineTests.UntilAsync(() => runtime.Deliveries.GetPersistentCounters().GetValueOrDefault($"connection/{connectionId}/mqtt/subscribed") == (phase + 1) * 2);
                Assert.Equal(hash, services.GetRequiredService<RuntimeVersionStore>().Load(1)!.ContentHash);
                foreach (var suffix in new[] { "module", "builtin" })
                { await MqttProbe.PublishAsync(peer, topic + "/" + suffix, "{\"value\":9007199254740993}"u8.ToArray(), MqttQualityOfServiceLevel.AtLeastOnce, Token); }
                var devices = new HashSet<string>();
                for (var index = 0; index < 2; index++)
                {
                    var message = await received.NextAsync(Token);
                    using var payload = JsonDocument.Parse(message.ApplicationMessage.ConvertPayloadToString());
                    Assert.Equal("9007199254740993", payload.RootElement.GetProperty("values").GetProperty("value").GetRawText());
                    devices.Add(payload.RootElement.GetProperty("deviceKey").GetString()!);
                }
                Assert.Equal(ExpectedDevices, devices.Order());
                await OutputPipelineTests.UntilAsync(() => runtime.Deliveries.GetStatus().DeletedRecords == 2 &&
                    runtime.Deliveries.GetPersistentCounters().GetValueOrDefault($"connection/{connectionId}/mqtt/acknowledged") == (phase + 1) * 2);
                Assert.All(runtime.GetStatus().Units, unit => Assert.Equal(RuntimeUnitState.Running, unit.State));
                // The counter records successful Topic Filter subscriptions, not TCP sessions.
                Assert.Equal((phase + 1) * 2, runtime.Deliveries.GetPersistentCounters()[$"connection/{connectionId}/mqtt/subscribed"]);
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ModbusModuleUsesSameOriginalConnectionAndUnitCoordinatorAsBuiltin(bool rtu)
    {
        await using var slave = ModbusTestSlave.Start(rtu ? ModbusFraming.RtuOverTcp : ModbusFraming.Tcp);
        using var data = TemporaryDirectory.Create();
        await using var host = Host(data.Path, "modbus", slave.Endpoint.Port);
        var services = host.Services;
        var connection = services.GetRequiredService<ConnectionDraftStore>().Write(null,
            new("0", "bus", "Bus", ConnectionKind.Modbus, Modbus: new("127.0.0.1", slave.Endpoint.Port,
                rtu ? ModbusFrameFormat.RtuOverTcp : ModbusFrameFormat.Tcp)), "test", null);
        var device = services.GetRequiredService<DeviceDraftStore>().Write(null,
            new(connection.Version, "module", "Module", DeviceKind.Module, ModuleKind: "input.testmodbus"), "test", null);
        var source = services.GetRequiredService<InputDraftStore>().Write(device.Device.Id, null,
            new(device.Version, "source", "Source", Module: new("input.testmodbus", 1, Json(new { unit = 1 }), new() { ["connection"] = connection.Connection.Id })), "test", null);
        var unit = services.GetRequiredService<InputModuleCatalog>().WithFrozenFields(source.Input.Module!.ToUnit("module", "source",
            new Dictionary<long, ConnectionDraft> { [connection.Connection.Id] = connection.Connection }));
        var delegated = InputModuleCatalog.ReadProtocol(unit)!;
        var builtin = new TenonEdge.Abstractions.Plans.RuntimeUnitPlan(TenonEdge.Abstractions.Plans.RuntimeUnitId.Create("input.modbus", "builtin/source"), delegated.Configuration);
        var document = RuntimePlanDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            units = new[]
        { new { kind = unit.Id.Kind, key = unit.Id.Key, configuration = unit.Configuration }, new { kind = builtin.Id.Kind, key = builtin.Id.Key, configuration = builtin.Configuration } }
        }),
            inputs: services.GetRequiredService<InputModuleCatalog>());
        var runtime = services.GetRequiredService<RuntimeHost>();
        await using var plan = runtime.Prepare(document.ToPlan(1));
        Assert.Empty(plan.Failures);
        Assert.Equal(0, slave.ConnectionCount);
        await runtime.ActivateAsync(plan);
        var message = await RuntimeTesting.ReadCapturedAsync(runtime, unit.Id);
        Assert.Equal(FieldValue.FromInt64(4096), message.Values["value"]);
        Assert.Equal(FieldValue.FromInt64(4096), (await RuntimeTesting.ReadCapturedAsync(runtime, builtin.Id)).Values["value"]);
        Assert.Equal(1, slave.ConnectionCount);
        await runtime.StopAsync(Token);
        Assert.Equal(0, runtime.InFlight.Status.ReservedBytes);
    }
}
