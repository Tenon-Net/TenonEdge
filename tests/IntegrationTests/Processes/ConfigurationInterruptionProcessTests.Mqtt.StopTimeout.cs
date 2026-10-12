using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using TenonEdge.Host.Bootstrap;
using TenonEdge.Host.Features.Audit;
using TenonEdge.Host.Storage;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.IntegrationTests.Probes.Mqtt;

namespace TenonEdge.IntegrationTests.Processes;

public sealed partial class ConfigurationInterruptionProcessTests
{
    [Fact]
    [Trait("Category", "Process")]
    [Trait("Category", "RealBroker")]
    public async Task SharedMqttStopTimeoutRecoversOldVersionOnlyAfterOriginalLoopExits()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "共享 MQTT 真实停止超时与进程恢复在 Linux 验证。");
        await using var broker = await MosquittoBrokerFixture.StartDedicatedAsync(Token);
        output.WriteLine($"TEST MQTT STOP TIMEOUT BROKER PORTS: {broker.Port},{broker.TlsPort}");
        using var work = TemporaryDirectory.Create();
        using var data = TemporaryDirectory.Create();
        using var receiver = new TcpListener(IPAddress.Loopback, 0);
        receiver.Start();
        var port = ((IPEndPoint)receiver.LocalEndpoint).Port;
        var prefix = MqttProbe.NewTopic("stop-timeout");
        var clientId = "stop-timeout-" + Guid.NewGuid().ToString("N");
        var (publisher, _) = await MqttProbe.ConnectAsync(MqttProbe.Options(broker, "publisher-" + Guid.NewGuid().ToString("N")), Token);
        using var peer = publisher;
        var messages = ReceivedMessages.Attach(peer);
        await MqttProbe.SubscribeAsync(peer, prefix + "/output", Token);
        var state = new RuntimeStateReader(data.Path);
        await using var host = HostProcess.StartMqttPartialStopProbe(work.Path, data.Path, MqttStopArguments(port, broker.Port));
        try
        {
            using var api = await ClientAsync(host, setup: true);
            var draft = await CreateMqttStopDraftAsync(api, port, broker, clientId, prefix, sharedOutput: true);
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
            var mqttFrozen = ReadMqttFanOutRows(data.Path, draft.ConnectionId);
            var httpFrozen = HttpFanOutRows(data.Path, mqttFrozen);
            var allFrozen = ReadDeliveries(data.Path);
            AssertMqttStopFrozen(httpFrozen, original);
            AssertMqttFanOutFrozen(mqttFrozen, httpFrozen, prefix + "/output");
            var version = await EditMqttStopSourceAsync(api, draft.DeviceId, draft.InputId, draft.Version, "sample", prefix + "/a-new");
            version = await EditMqttStopSourceAsync(api, draft.SlowDeviceId, draft.SlowInputId, version, "slow-stop", prefix + "/b-new");
            File.WriteAllText(Path.Combine(work.Path, MqttStopArmed), "armed");
            var applying = await BeginApplyAsync(api, version);
            await WaitForGateAsync(work.Path, MqttStopEntered, "input.mqtt/meter-slow/slow-stop");
            var waiting = Stopwatch.StartNew();
            var quiet = await WaitForAsync(api, Api + "/runtime/status", status =>
                MqttStopUnit(status, "meter/sample").GetProperty("state").GetString() == "stopped" &&
                MqttStopUnit(status, "meter-slow/slow-stop").GetProperty("state").GetString() == "stopping");
            await WaitForMqttStopDisconnectAsync(broker, clientId);
            await SetMqttStopOutputPauseAsync(api, draft.ConnectionId, paused: false);
            await AssertMqttStopOutputQuiescingAsync(api, quiet);
            var dirtyVersion = await EditMqttStopSourceAsync(api, draft.DeviceId, draft.InputId, version, "sample", prefix + "/a-dirty");
            dirtyVersion = await EditMqttStopSourceAsync(api, draft.SlowDeviceId, draft.SlowInputId, dirtyVersion, "slow-stop", prefix + "/b-dirty");

            var timedOut = await WaitForAsync(api, Api + "/runtime/status", status =>
                MqttStopUnit(status, "meter-slow/slow-stop").GetProperty("state").GetString() == "stopTimedOut");
            output.WriteLine($"TEST MQTT STOP TIMEOUT FIRST DEADLINE: {waiting.Elapsed.TotalSeconds:F3}s since gate observed; product deadline unchanged at 5s.");
            var failed = await WaitForAsync(api, applying, operation => operation.GetProperty("stage").GetString() == "failed" &&
                host.Output.Contains("TEST MQTT PARTIAL STOP UNSTARTED UNIT DISPOSED: input.mqtt/meter-slow/slow-stop", StringComparison.Ordinal));
            Assert.False(failed.GetProperty("committed").GetBoolean());
            Assert.Equal("runtime.quiescenceFailed", failed.GetProperty("errorCode").GetString());
            var result = failed.GetProperty("result");
            Assert.Equal(2, result.GetProperty("revision").GetInt64());
            Assert.False(result.GetProperty("committed").GetBoolean());
            Assert.False(result.GetProperty("activated").GetBoolean());
            Assert.True(result.GetProperty("recoveryPending").GetBoolean());
            Assert.Equal("runtime.quiescenceFailed", result.GetProperty("code").GetString());
            var blocked = await api.GetJsonAsync(Api + "/runtime/status", Token);
            Assert.Equal("stopped", MqttStopUnit(blocked, "meter/sample").GetProperty("state").GetString());
            Assert.Equal("stopTimedOut", MqttStopUnit(blocked, "meter-slow/slow-stop").GetProperty("state").GetString());
            Assert.Equal(2, blocked.GetProperty("runtime").GetProperty("units").EnumerateArray().Count(unit =>
                unit.GetProperty("id").GetProperty("kind").GetString() == "input.mqtt"));
            Assert.Equal(1, timedOut.GetProperty("runtime").GetProperty("activeRevision").GetInt64());
            Assert.Equal(0, blocked.GetProperty("inFlight").GetProperty("reservedBytes").GetInt64());
            Assert.Equal((1L, (long?)null, (long?)null, (long?)1),
                (state.Pointers().Effective, state.Pointers().Previous, state.Pointers().Pending, state.Pointers().Running));
            Assert.Equal(RuntimeSnapshotStates.NotEffective, state.Snapshot(2).State);
            Assert.Equal("runtime.quiescenceFailed", state.Snapshot(2).NotEffectiveReason);
            var events = await api.GetJsonAsync(Api + "/runtime/events?type=runtime.unitStopTimedOut&target=input.mqtt%2Fmeter-slow%2Fslow-stop", Token);
            Assert.Equal(1, Assert.Single(events.GetProperty("items").EnumerateArray()).GetProperty("occurrenceCount").GetInt32());
            Assert.DoesNotContain("TEST MQTT PARTIAL STOP ORIGINAL STOPPED", host.Output, StringComparison.Ordinal);
            Assert.DoesNotContain("TEST MQTT PARTIAL STOP UNIT DISPOSED", host.Output, StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(work.Path, MqttStopReleased)));
            Assert.Equal(allFrozen, ReadDeliveries(data.Path));
            Assert.Equal(mqttFrozen, ReadMqttFanOutRows(data.Path, draft.ConnectionId));
            Assert.Empty(await messages.CollectAsync(TimeSpan.Zero, Token));
            Assert.Single((await broker.LogsAsync(Token)).Split('\n'), line => line.Contains($" as {clientId} (", StringComparison.Ordinal));
            // 发布者的确认只证明存活 Broker 接收；旧循环还未退出，网关并未接管这些离线消息。
            await PublishMqttStopAsync(peer, prefix + "/a-old", 84);
            await PublishMqttStopAsync(peer, prefix + "/b-old", 86);
            Assert.Equal(2, MqttCounter(data.Path, draft.ConnectionId, "acknowledged"));
            Assert.Equal(allFrozen, ReadDeliveries(data.Path));
            output.WriteLine("TEST MQTT STOP TIMEOUT BLOCKED: failed/committed=false/recoveryPending=true; r1 effective/running, r2 notEffective; original slow loop alive; no replacement MQTT connection or publication; frozen MQTT2+HTTP2 unchanged; offline84/86 still at Broker.");

            File.Delete(Path.Combine(work.Path, MqttStopArmed));
            File.WriteAllText(Path.Combine(work.Path, MqttStopReleased), "released after application failure");
            var recovered = await WaitForAsync(api, Api + "/runtime/status", status =>
                MqttStopUnit(status, "meter/sample").GetProperty("state").GetString() == "running" &&
                MqttStopUnit(status, "meter-slow/slow-stop").GetProperty("state").GetString() == "running" &&
                MqttCounter(data.Path, draft.ConnectionId, "subscribed") == 4 &&
                host.Output.Contains("TEST MQTT PARTIAL STOP ORIGINAL STOPPED: input.mqtt/meter-slow/slow-stop", StringComparison.Ordinal) &&
                host.Output.Contains("TEST MQTT PARTIAL STOP UNIT DISPOSED: input.mqtt/meter-slow/slow-stop", StringComparison.Ordinal));
            AssertMqttStopRunning(recovered);
            Assert.Equal(1, recovered.GetProperty("runtime").GetProperty("activeRevision").GetInt64());
            Assert.Contains("TEST MQTT PARTIAL STOP ORIGINAL STOPPED: input.mqtt/meter-slow/slow-stop", host.Output, StringComparison.Ordinal);
            Assert.Contains("TEST MQTT PARTIAL STOP UNIT DISPOSED: input.mqtt/meter-slow/slow-stop", host.Output, StringComparison.Ordinal);
            await WaitForAsync(api, Api + "/runtime/status", _ => MqttCounter(data.Path, draft.ConnectionId, "acknowledged") == 4);
            Assert.All(ReadDeliveries(data.Path), row => Assert.Equal(1, row.Revision));
            await AckAsync(accepted.GetStream());
            await ReplayMqttStopFrozenAsync(receiver, httpFrozen.Skip(1));
            await ReceiveMqttFanOutFrozenAsync(messages, mqttFrozen);
            var offline = await ReceiveMqttStopValuesAsync(receiver, httpFrozen, [84, 86]);
            await ReceiveMqttFanOutSamplesAsync(messages, prefix + "/output", offline);
            await PublishMqttStopAsync(peer, prefix + "/a-new", 126);
            await PublishMqttStopAsync(peer, prefix + "/b-new", 127);
            await PublishMqttStopAsync(peer, prefix + "/a-dirty", 128);
            await PublishMqttStopAsync(peer, prefix + "/b-dirty", 129);
            await PublishMqttStopAsync(peer, prefix + "/a-old", 168);
            await PublishMqttStopAsync(peer, prefix + "/b-old", 169);
            var live = await ReceiveMqttStopValuesAsync(receiver, httpFrozen, [168, 169]);
            await ReceiveMqttFanOutSamplesAsync(messages, prefix + "/output", live);
            await WaitForAsync(api, Api + "/runtime/status", _ => ReadDeliveries(data.Path).Length == 0 &&
                MqttCounter(data.Path, draft.ConnectionId, "acknowledged") == 6);
            Assert.Empty(await messages.CollectAsync(TimeSpan.Zero, Token));
            await AssertMqttStopSessionAsync(broker, data.Path, draft.ConnectionId, clientId);
            await AssertMqttStopOutputResumedAsync(api, state, draft.ConnectionId);
            await AssertMqttStopDirtyAsync(api, draft, prefix, dirtyVersion);
            Assert.Equal(["r1"], state.Audit(AuditActions.SnapshotApplied).Select(entry => entry.Target).ToArray());
            Assert.Empty(state.Audit(AuditActions.AutoRollback));
            Assert.Equal(2, state.Snapshots().Count);
            events = await api.GetJsonAsync(Api + "/runtime/events?type=runtime.unitStopTimedOut&target=input.mqtt%2Fmeter-slow%2Fslow-stop", Token);
            Assert.Equal(1, Assert.Single(events.GetProperty("items").EnumerateArray()).GetProperty("occurrenceCount").GetInt32());
            await host.RequestStopAsync(Token);
            Assert.Equal(HostExitCodes.Success, await host.WaitForExitAsync(Deadline, Token));
            Assert.Null(state.Pointers().Running);
            Assert.Equal(0, state.Snapshot(1).AbnormalExitCount);
            Assert.Equal(0, state.Snapshot(2).AbnormalExitCount);
            Assert.Contains("在 00:00:05 内没有停止", host.Output, StringComparison.Ordinal);
            output.WriteLine("TEST MQTT STOP TIMEOUT RECOVERED: same process; r1 two inputs restored only after original loop exit/disposal; MQTT+HTTP frozen42/43 and offline84/86/live168/169; new/dirty ignored; no new snapshot or automatic rollback; timeout event count1; manual pause false; queue empty; SIGTERM0.");
        }
        finally
        {
            // 断言失败也先释放本测试的同步点，让原单元退出；不靠探针自停或抛弃仍在运行的循环清理。
            File.Delete(Path.Combine(work.Path, MqttStopArmed));
            if (!File.Exists(Path.Combine(work.Path, MqttStopReleased)))
            { File.WriteAllText(Path.Combine(work.Path, MqttStopReleased), "test cleanup"); }
            output.WriteLine(host.Output);
            output.WriteLine(await broker.LogsAsync(Token));
        }
    }
}
