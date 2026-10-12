using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using TenonEdge.Host.Bootstrap;
using TenonEdge.Host.Features.Audit;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Storage;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.IntegrationTests.Probes.Mqtt;

namespace TenonEdge.IntegrationTests.Processes;

public sealed partial class ConfigurationInterruptionProcessTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [Trait("Category", "Process")]
    [Trait("Category", "RealBroker")]
    public async Task SharedMqttPartialActivationWaitsForAllOriginalInputsBeforeConnecting(bool interrupt)
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "共享 MQTT 部分激活及真实强杀恢复在 Linux 验证。");
        await using var broker = await MosquittoBrokerFixture.StartDedicatedAsync(Token);
        output.WriteLine($"TEST MQTT PARTIAL ACTIVATION BROKER PORTS: {broker.Port},{broker.TlsPort}");
        using var work = TemporaryDirectory.Create();
        using var data = TemporaryDirectory.Create();
        using var receiver = new TcpListener(IPAddress.Loopback, 0);
        receiver.Start();
        var port = ((IPEndPoint)receiver.LocalEndpoint).Port;
        var prefix = MqttProbe.NewTopic("partial-activation");
        var clientId = "partial-activation-" + Guid.NewGuid().ToString("N");
        var (publisher, _) = await MqttProbe.ConnectAsync(MqttProbe.Options(broker, "publisher-" + Guid.NewGuid().ToString("N")), Token);
        using var peer = publisher;
        var messages = ReceivedMessages.Attach(peer);
        await MqttProbe.SubscribeAsync(peer, prefix + "/output", Token);
        var state = new RuntimeStateReader(data.Path);
        MqttStopDraft draft;
        string dirtyVersion;
        MqttFanOutRow[] mqttFrozen;
        DeliveryRow[] httpFrozen;
        DeliveryRow[] frozen;
        await using (var host = HostProcess.StartMqttPartialActivationProbe(work.Path, data.Path, MqttStopArguments(port, broker.Port)))
        {
            var killed = false;
            try
            {
                using var api = await ClientAsync(host, setup: true);
                draft = await CreateMqttStopDraftAsync(api, port, broker, clientId, prefix, sharedOutput: true);
                var initial = await BeginApplyAsync(api, draft.Version);
                await WaitForAsync(api, initial, operation => operation.GetProperty("stage").GetString() == "completed");
                await WaitForAsync(api, Api + "/runtime/status", _ => MqttCounter(data.Path, draft.ConnectionId, "subscribed") == 2);
                await SetMqttStopOutputPauseAsync(api, draft.ConnectionId, paused: true);
                await PublishMqttStopAsync(peer, prefix + "/a-old", 42);
                using var accepted = await AcceptAsync(receiver);
                var original = await ReadRequestAsync(accepted.GetStream());
                await PublishMqttStopAsync(peer, prefix + "/b-old", 43);
                await WaitForAsync(api, Api + "/runtime/status", _ => ReadDeliveries(data.Path).Length == 4 &&
                    MqttCounter(data.Path, draft.ConnectionId, "acknowledged") == 2);
                mqttFrozen = ReadMqttFanOutRows(data.Path, draft.ConnectionId);
                httpFrozen = HttpFanOutRows(data.Path, mqttFrozen);
                frozen = ReadDeliveries(data.Path);
                AssertMqttStopFrozen(httpFrozen, original);
                AssertMqttFanOutFrozen(mqttFrozen, httpFrozen, prefix + "/output");
                var version = await EditMqttStopSourceAsync(api, draft.DeviceId, draft.InputId, draft.Version, "sample", prefix + "/a-new");
                version = await EditMqttStopSourceAsync(api, draft.SlowDeviceId, draft.SlowInputId, version, "slow-stop", prefix + "/b-new");
                File.WriteAllText(Path.Combine(work.Path, PartialActivationControl.ArmedFile), "armed");
                var applying = await BeginApplyAsync(api, version);
                await WaitForGateAsync(work.Path, PartialActivationControl.InputEnteredFile, "input.mqtt/meter-slow/slow-stop");
                await WaitForGateAsync(work.Path, PartialActivationControl.LifecycleEnteredFile, PartialActivationControl.EventType);
                await WaitForMqttStopDisconnectAsync(broker, clientId);
                await SetMqttStopOutputPauseAsync(api, draft.ConnectionId, paused: false);
                var quiet = await WaitForAsync(api, Api + "/runtime/status", status =>
                    MqttStopUnit(status, "meter/sample").GetProperty("reads").GetProperty("readsFailed").GetInt64() >= 1);
                await AssertMqttPartialActivationRetryAsync(api, quiet);
                var operation = await api.GetJsonAsync(applying, Token);
                Assert.Equal("activating", operation.GetProperty("stage").GetString());
                Assert.True(operation.GetProperty("committed").GetBoolean());
                AssertMqttStopRunning(quiet); // 两个包装单元已受监督；不谎称原慢循环已经调用。
                Assert.Equal(2, quiet.GetProperty("runtime").GetProperty("activeRevision").GetInt64());
                Assert.Equal(0, MqttStopUnit(quiet, "meter-slow/slow-stop").GetProperty("reads").GetProperty("readsFailed").GetInt64());
                Assert.Equal(0, MqttStopUnit(quiet, "meter-slow/slow-stop").GetProperty("reads").GetProperty("readsCompleted").GetInt64());
                Assert.Equal(0, quiet.GetProperty("inFlight").GetProperty("reservedBytes").GetInt64());
                Assert.Equal((2L, (long?)1, (long?)null, (long?)2),
                    (state.Pointers().Effective, state.Pointers().Previous, state.Pointers().Pending, state.Pointers().Running));
                Assert.Equal(RuntimeSnapshotStates.History, state.Snapshot(1).State);
                Assert.Equal(RuntimeSnapshotStates.Effective, state.Snapshot(2).State);
                Assert.False(File.Exists(Path.Combine(work.Path, PartialActivationControl.InputReleasedFile)));
                Assert.False(File.Exists(Path.Combine(work.Path, PartialActivationControl.LifecycleReleasedFile)));
                Assert.Equal(frozen, ReadDeliveries(data.Path));
                Assert.Equal(mqttFrozen, ReadMqttFanOutRows(data.Path, draft.ConnectionId));
                Assert.Equal(2, MqttCounter(data.Path, draft.ConnectionId, "subscribed"));
                Assert.Single((await broker.LogsAsync(Token)).Split('\n'), line => line.Contains($" as {clientId} (", StringComparison.Ordinal));
                Assert.Empty(await messages.CollectAsync(TimeSpan.Zero, Token));
                // 原持久会话仍订阅 old；此确认只证明 Broker 接收，不是新版本已经接管。
                await PublishMqttStopAsync(peer, prefix + "/a-old", 126);
                await PublishMqttStopAsync(peer, prefix + "/b-old", 127);
                dirtyVersion = await EditMqttStopSourceAsync(api, draft.DeviceId, draft.InputId, version, "sample", prefix + "/a-dirty");
                dirtyVersion = await EditMqttStopSourceAsync(api, draft.SlowDeviceId, draft.SlowInputId, dirtyVersion, "slow-stop", prefix + "/b-dirty");
                Assert.Equal(2, MqttCounter(data.Path, draft.ConnectionId, "acknowledged"));
                output.WriteLine("TEST MQTT PARTIAL ACTIVATION BLOCKED: r2 activating/committed=true; both supervised running, original slow loop not called; fast input deadline + real output retry; manual pause false; Broker connections1/subscribed2; no new wire publication or local takeover; MQTT2+HTTP2 r1 frozen unchanged; old126/127 only at Broker; dirty topics preserved.");
                if (!interrupt)
                {
                    ReleaseMqttPartialActivation(work.Path);
                    var completed = await WaitForAsync(api, applying, result => result.GetProperty("stage").GetString() == "completed");
                    Assert.True(completed.GetProperty("committed").GetBoolean());
                    await WaitForAsync(api, Api + "/runtime/status", _ => MqttCounter(data.Path, draft.ConnectionId, "subscribed") == 4);
                    // 与强杀重启不同：原 HTTP 发送者未停止，队头仍在原 socket 上等待确认。
                    await AckAsync(accepted.GetStream());
                    await ReplayMqttStopFrozenAsync(receiver, httpFrozen.Skip(1));
                    await ReceiveMqttFanOutFrozenAsync(messages, mqttFrozen);
                    await WaitForAsync(api, Api + "/runtime/status", _ => MqttCounter(data.Path, draft.ConnectionId, "unmatched") == 2 &&
                        MqttCounter(data.Path, draft.ConnectionId, "acknowledged") == 4);
                    await PublishMqttStopAsync(peer, prefix + "/a-new", 84);
                    await PublishMqttStopAsync(peer, prefix + "/b-new", 86);
                    var fresh = await ReceiveMqttStopValuesAsync(receiver, httpFrozen, [84, 86]);
                    await ReceiveMqttFanOutSamplesAsync(messages, prefix + "/output", fresh);
                    await PublishMqttStopAsync(peer, prefix + "/a-old", 252);
                    await PublishMqttStopAsync(peer, prefix + "/b-old", 253);
                    await PublishMqttStopAsync(peer, prefix + "/a-dirty", 128);
                    await PublishMqttStopAsync(peer, prefix + "/b-dirty", 129);
                    await PublishMqttStopAsync(peer, prefix + "/a-new", 168);
                    await PublishMqttStopAsync(peer, prefix + "/b-new", 169);
                    var live = await ReceiveMqttStopValuesAsync(receiver, httpFrozen, [168, 169]);
                    await ReceiveMqttFanOutSamplesAsync(messages, prefix + "/output", live);
                    var running = await WaitForAsync(api, Api + "/runtime/status", _ => ReadDeliveries(data.Path).Length == 0 &&
                        MqttCounter(data.Path, draft.ConnectionId, "acknowledged") == 8);
                    AssertMqttStopRunning(running);
                    Assert.Equal(2, running.GetProperty("runtime").GetProperty("activeRevision").GetInt64());
                    Assert.Equal(2, MqttCounter(data.Path, draft.ConnectionId, "unmatched"));
                    await AssertMqttStopSessionAsync(broker, data.Path, draft.ConnectionId, clientId);
                    await AssertMqttStopDirtyAsync(api, draft, prefix, dirtyVersion);
                    await AssertMqttStopOutputResumedAsync(api, state, draft.ConnectionId);
                    Assert.Equal(2, state.Snapshots().Count);
                    Assert.Equal(["r1", "r2"], state.Audit(AuditActions.SnapshotApplied).Select(entry => entry.Target).ToArray());
                    Assert.Empty(state.Audit(AuditActions.AutoRollback));
                    Assert.Empty(state.Events(RuntimeEventTypes.VersionNotEffective));
                    Assert.Empty(await messages.CollectAsync(TimeSpan.Zero, Token));
                    await host.RequestStopAsync(Token);
                    Assert.Equal(HostExitCodes.Success, await host.WaitForExitAsync(Deadline, Token));
                    Assert.Null(state.Pointers().Running);
                    Assert.Equal(0, state.Snapshot(1).AbnormalExitCount);
                    Assert.Equal(0, state.Snapshot(2).AbnormalExitCount);
                    Assert.Contains("TEST PARTIAL ACTIVATION INPUT RELEASED: input.mqtt/meter-slow/slow-stop", host.Output, StringComparison.Ordinal);
                    Assert.Contains("TEST PARTIAL ACTIVATION LIFECYCLE RELEASED", host.Output, StringComparison.Ordinal);
                    Assert.DoesNotContain("TEST PARTIAL ACTIVATION LIFECYCLE DEADLINE EXPIRED", host.Output, StringComparison.Ordinal);
                    output.WriteLine("TEST MQTT PARTIAL ACTIVATION RELEASED: both original input loops started; r2 completed/committed=true; original HTTP socket acknowledged; MQTT+HTTP r1 frozen42/43 unchanged FIFO; offline old126/127 unmatched+acknowledged; new84/86/live168/169 shared source identities and distinct delivery identities; old/dirty live ignored; connections2/subscribed4/sessionLost0; dirty preserved; no new snapshot/automatic rollback; abnormal counts0; manual pause false; queue empty; SIGTERM0.");
                    return;
                }
                using var queue = OpenQueue(data.Path);
                using var held = queue.BeginTransaction(deferred: false);
                Assert.Equal(frozen, ReadDeliveries(data.Path));
                await host.KillAsync(Token);
                killed = true;
                held.Rollback();
                Assert.Equal(frozen, ReadDeliveries(data.Path));
                Assert.DoesNotContain("TEST PARTIAL ACTIVATION INPUT RELEASED", host.Output, StringComparison.Ordinal);
                Assert.DoesNotContain("TEST PARTIAL ACTIVATION LIFECYCLE RELEASED", host.Output, StringComparison.Ordinal);
            }
            finally
            {
                if (!killed)
                { ReleaseMqttPartialActivation(work.Path); output.WriteLine(await broker.LogsAsync(Token)); }
                output.WriteLine(host.Output);
            }
        }

        await using var restarted = HostProcess.Start(work.Path, "Production", data.Path, MqttStopArguments(port, broker.Port));
        try
        {
            using var api = await ClientAsync(restarted, setup: false);
            Assert.Equal((2L, (long?)1, (long?)null, (long?)2),
                (state.Pointers().Effective, state.Pointers().Previous, state.Pointers().Pending, state.Pointers().Running));
            Assert.Equal(1, state.Snapshot(2).AbnormalExitCount);
            Assert.Equal(0, state.Snapshot(1).AbnormalExitCount);
            Assert.Equal(RuntimeSnapshotStates.Effective, state.Snapshot(2).State);
            Assert.Null(state.Snapshot(2).NotEffectiveReason);
            Assert.Equal(["r1", "r2"], state.Audit(AuditActions.SnapshotApplied).Select(entry => entry.Target).ToArray());
            Assert.Empty(state.Audit(AuditActions.AutoRollback));
            Assert.Empty(state.Events(RuntimeEventTypes.VersionNotEffective));
            await ReplayMqttStopFrozenAsync(receiver, httpFrozen);
            await ReceiveMqttFanOutFrozenAsync(messages, mqttFrozen);
            await WaitForAsync(api, Api + "/runtime/status", _ => MqttCounter(data.Path, draft.ConnectionId, "unmatched") == 2 &&
                MqttCounter(data.Path, draft.ConnectionId, "acknowledged") == 4);
            await PublishMqttStopAsync(peer, prefix + "/a-new", 84);
            await PublishMqttStopAsync(peer, prefix + "/b-new", 86);
            var fresh = await ReceiveMqttStopValuesAsync(receiver, httpFrozen, [84, 86]);
            await ReceiveMqttFanOutSamplesAsync(messages, prefix + "/output", fresh);
            await PublishMqttStopAsync(peer, prefix + "/a-old", 252);
            await PublishMqttStopAsync(peer, prefix + "/b-old", 253);
            await PublishMqttStopAsync(peer, prefix + "/a-dirty", 128);
            await PublishMqttStopAsync(peer, prefix + "/b-dirty", 129);
            await PublishMqttStopAsync(peer, prefix + "/a-new", 168);
            await PublishMqttStopAsync(peer, prefix + "/b-new", 169);
            var live = await ReceiveMqttStopValuesAsync(receiver, httpFrozen, [168, 169]);
            await ReceiveMqttFanOutSamplesAsync(messages, prefix + "/output", live);
            var running = await WaitForAsync(api, Api + "/runtime/status", _ => ReadDeliveries(data.Path).Length == 0 &&
                MqttCounter(data.Path, draft.ConnectionId, "acknowledged") == 8);
            AssertMqttStopRunning(running);
            Assert.Equal(2, running.GetProperty("runtime").GetProperty("activeRevision").GetInt64());
            Assert.Equal(2, MqttCounter(data.Path, draft.ConnectionId, "unmatched"));
            await AssertMqttStopSessionAsync(broker, data.Path, draft.ConnectionId, clientId);
            await AssertMqttStopDirtyAsync(api, draft, prefix, dirtyVersion);
            await AssertMqttStopOutputResumedAsync(api, state, draft.ConnectionId);
            Assert.Equal(2, state.Snapshots().Count);
            Assert.Empty(await messages.CollectAsync(TimeSpan.Zero, Token));
            await restarted.RequestStopAsync(Token);
            Assert.Equal(HostExitCodes.Success, await restarted.WaitForExitAsync(Deadline, Token));
            Assert.Null(state.Pointers().Running);
            output.WriteLine("TEST MQTT PARTIAL ACTIVATION RECOVERED: original product Host r2; MQTT+HTTP r1 frozen42/43 unchanged FIFO; old offline126/127 unmatched+acknowledged, never a delivery; new84/86/live168/169 shared source identities and distinct delivery identities; old/dirty live ignored; connections2/subscribed4/sessionLost0; dirty preserved; no new snapshot/automatic rollback; r2 abnormal1/r1 abnormal0; manual pause false; queue empty; SIGTERM0.");
        }
        finally { output.WriteLine(restarted.Output); output.WriteLine(await broker.LogsAsync(Token)); }
    }

    private static async Task AssertMqttPartialActivationRetryAsync(HttpClient api, JsonElement status)
    {
        var sender = Assert.Single(status.GetProperty("runtime").GetProperty("units").EnumerateArray(), unit =>
            unit.GetProperty("id").GetProperty("kind").GetString() == "output.mqtt");
        Assert.Equal("running", sender.GetProperty("state").GetString());
        var id = sender.GetProperty("id");
        var target = $"{id.GetProperty("kind").GetString()}/{id.GetProperty("key").GetString()}";
        const string expected = "MQTT 排队或网络操作超时，记录保持待发。";
        await WaitForAsync(api, $"{Api}/runtime/events?type=output.retry&target={Uri.EscapeDataString(target)}", page =>
            page.GetProperty("items").EnumerateArray().Any(item => item.GetProperty("message").GetString() == expected));
    }

    private static void ReleaseMqttPartialActivation(string directory)
    {
        File.Delete(Path.Combine(directory, PartialActivationControl.ArmedFile));
        File.WriteAllText(Path.Combine(directory, PartialActivationControl.InputReleasedFile), "release original MQTT input");
        File.WriteAllText(Path.Combine(directory, PartialActivationControl.LifecycleReleasedFile), "release original activation event");
    }
}
