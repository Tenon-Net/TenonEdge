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
    [Fact]
    [Trait("Category", "Process")]
    [Trait("Category", "RealBroker")]
    public async Task SharedMqttStartupFaultDoesNotRollBackAndRestartsOnlyFailedInput()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "共享 MQTT 输入启动故障及原监督者同进程恢复在 Linux 验证。");
        await using var broker = await MosquittoBrokerFixture.StartDedicatedAsync(Token);
        output.WriteLine($"TEST MQTT STARTUP FAULT BROKER PORTS: {broker.Port},{broker.TlsPort}");
        using var work = TemporaryDirectory.Create();
        using var data = TemporaryDirectory.Create();
        using var receiver = new TcpListener(IPAddress.Loopback, 0);
        receiver.Start();
        var port = ((IPEndPoint)receiver.LocalEndpoint).Port;
        var prefix = MqttProbe.NewTopic("startup-fault");
        var clientId = "startup-fault-" + Guid.NewGuid().ToString("N");
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
                File.WriteAllText(Path.Combine(work.Path, PartialActivationControl.FaultRequestedFile), "fail first original loop call");
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
                output.WriteLine("TEST MQTT STARTUP FAULT BEFORE: r2 activating/committed=true; original slow loop not called; real fast deadline + output retry; connections1/subscribed2; frozen MQTT2+HTTP2 unchanged; old126/127 only at Broker; dirty retained.");
                File.WriteAllText(Path.Combine(work.Path, PartialActivationControl.InputReleasedFile), "inject startup exception");
                await WaitForGateAsync(work.Path, PartialActivationControl.FaultDisposedFile, "input.mqtt/meter-slow/slow-stop");
                await WaitForGateAsync(work.Path, PartialActivationControl.RestartEnteredFile, "input.mqtt/meter-slow/slow-stop");
                // 由原监督者实际退避/重建；原失败实例已释放，下一实例的原循环尚未调用。
                ReleaseMqttPartialActivation(work.Path);
                var completed = await WaitForAsync(api, applying, result => result.GetProperty("stage").GetString() == "completed");
                Assert.True(completed.GetProperty("committed").GetBoolean());
                await WaitForAsync(api, Api + "/runtime/status", _ => MqttCounter(data.Path, draft.ConnectionId, "subscribed") == 4);
                await AckAsync(accepted.GetStream());
                await ReplayMqttStopFrozenAsync(receiver, httpFrozen.Skip(1));
                await ReceiveMqttFanOutFrozenAsync(messages, mqttFrozen);
                await WaitForAsync(api, Api + "/runtime/status", _ => MqttCounter(data.Path, draft.ConnectionId, "unmatched") == 2 &&
                    MqttCounter(data.Path, draft.ConnectionId, "acknowledged") == 4);
                await PublishMqttStopAsync(peer, prefix + "/a-new", 84);
                var fast = await ReceiveMqttStopValuesAsync(receiver, httpFrozen, [84]);
                await ReceiveMqttFanOutSamplesAsync(messages, prefix + "/output", fast);
                var degraded = await WaitForAsync(api, Api + "/runtime/status", _ => ReadDeliveries(data.Path).Length == 0 &&
                    MqttCounter(data.Path, draft.ConnectionId, "acknowledged") == 5);
                AssertMqttStopRunning(degraded); // 慢包装实例已受监督，原重试循环仍被测试 gate 延后。
                Assert.Equal(2, degraded.GetProperty("runtime").GetProperty("activeRevision").GetInt64());
                var slow = MqttStopUnit(degraded, "meter-slow/slow-stop");
                Assert.Equal(1, slow.GetProperty("restartCount").GetInt32());
                Assert.NotEqual(JsonValueKind.Null, slow.GetProperty("lastError").ValueKind);
                Assert.Equal(0, slow.GetProperty("reads").GetProperty("readsCompleted").GetInt64());
                Assert.Equal(0, slow.GetProperty("reads").GetProperty("readsFailed").GetInt64());
                Assert.Equal(0, MqttStopUnit(degraded, "meter/sample").GetProperty("restartCount").GetInt32());
                var fault = Assert.Single(state.Events("runtime.unitFaulted"));
                Assert.Equal("input.mqtt/meter-slow/slow-stop", fault.Target);
                Assert.Equal(1, fault.Count);
                Assert.Contains("秒后重启（连续第 1 次）", fault.Message, StringComparison.Ordinal);
                Assert.Empty(state.Events("runtime.unitBuildFailed"));
                Assert.False(File.Exists(Path.Combine(work.Path, PartialActivationControl.RestartReleasedFile)));
                Assert.DoesNotContain("TEST MQTT STARTUP RETRY RELEASED", host.Output, StringComparison.Ordinal);
                Assert.DoesNotContain("仅测试：原 MQTT 输入循环调用前的启动异常。", degraded.GetRawText(), StringComparison.Ordinal);
                await AssertMqttStopSessionAsync(broker, data.Path, draft.ConnectionId, clientId);
                output.WriteLine("TEST MQTT STARTUP FAULT DEGRADED: original fault disposed; original supervisor backoff/rebuild, only slow restartCount1; retry original loop still not called; r2 completed/committed=true; original HTTP socket + frozen MQTT2/HTTP2 acknowledged unchanged; offline old126/127 unmatched; fast84 delivered to both targets; connections2/subscribed4/sessionLost0; no rollback.");
                File.WriteAllText(Path.Combine(work.Path, PartialActivationControl.RestartReleasedFile), "run rebuilt original input");
                await WaitForGateAsync(work.Path, PartialActivationControl.RestartOriginalCalledFile, "input.mqtt/meter-slow/slow-stop");
                await PublishMqttStopAsync(peer, prefix + "/b-new", 86);
                var recovered = await ReceiveMqttStopValuesAsync(receiver, httpFrozen, [86]);
                await ReceiveMqttFanOutSamplesAsync(messages, prefix + "/output", recovered);
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
                Assert.Equal(1, MqttStopUnit(running, "meter-slow/slow-stop").GetProperty("restartCount").GetInt32());
                Assert.Equal(0, MqttStopUnit(running, "meter/sample").GetProperty("restartCount").GetInt32());
                Assert.Equal(2, MqttStopUnit(running, "meter/sample").GetProperty("reads").GetProperty("readsCompleted").GetInt64());
                Assert.Equal(2, MqttStopUnit(running, "meter-slow/slow-stop").GetProperty("reads").GetProperty("readsCompleted").GetInt64());
                Assert.Equal(2, MqttCounter(data.Path, draft.ConnectionId, "unmatched"));
                await AssertMqttStopSessionAsync(broker, data.Path, draft.ConnectionId, clientId);
                await AssertMqttStopDirtyAsync(api, draft, prefix, dirtyVersion);
                await AssertMqttStopOutputResumedAsync(api, state, draft.ConnectionId);
                Assert.Equal((2L, (long?)1, (long?)null, (long?)2),
                    (state.Pointers().Effective, state.Pointers().Previous, state.Pointers().Pending, state.Pointers().Running));
                Assert.Equal(2, state.Snapshots().Count);
                Assert.Equal(RuntimeSnapshotStates.Effective, state.Snapshot(2).State);
                Assert.Null(state.Snapshot(2).NotEffectiveReason);
                Assert.Equal(["r1", "r2"], state.Audit(AuditActions.SnapshotApplied).Select(entry => entry.Target).ToArray());
                Assert.Empty(state.Audit(AuditActions.AutoRollback));
                Assert.Empty(state.Events(RuntimeEventTypes.VersionNotEffective));
                Assert.Single(state.Events("runtime.unitFaulted"));
                Assert.Empty(await messages.CollectAsync(TimeSpan.Zero, Token));
                await host.RequestStopAsync(Token);
                Assert.Equal(HostExitCodes.Success, await host.WaitForExitAsync(Deadline, Token));
                Assert.Null(state.Pointers().Running);
                Assert.Equal(0, state.Snapshot(1).AbnormalExitCount);
                Assert.Equal(0, state.Snapshot(2).AbnormalExitCount);
                var injected = host.Output.IndexOf("TEST MQTT STARTUP FAULT INJECTED", StringComparison.Ordinal);
                var disposed = host.Output.IndexOf("TEST MQTT STARTUP FAULT ORIGINAL DISPOSED", StringComparison.Ordinal);
                var retry = host.Output.IndexOf("TEST MQTT STARTUP RETRY ENTERED", StringComparison.Ordinal);
                var released = host.Output.IndexOf("TEST MQTT STARTUP RETRY RELEASED", StringComparison.Ordinal);
                Assert.True(injected >= 0 && injected < disposed && disposed < retry && retry < released, host.Output);
                Assert.DoesNotContain("TEST PARTIAL ACTIVATION LIFECYCLE DEADLINE EXPIRED", host.Output, StringComparison.Ordinal);
                output.WriteLine("TEST MQTT STARTUP FAULT RECOVERED: same process r2; original supervisor rebuilt only failed slow input after disposal/backoff; real slow86 + fast/slow live168/169 fan out to MQTT+HTTP with shared source IDs/distinct delivery IDs; old/dirty ignored; connections2/subscribed4/acknowledged8/unmatched2/sessionLost0; no new snapshot/automatic rollback; abnormal counts0; dirty retained; pause false; queue empty; SIGTERM0.");
            }
            finally
            {
                ReleaseMqttPartialActivation(work.Path);
                File.WriteAllText(Path.Combine(work.Path, PartialActivationControl.RestartReleasedFile), "cleanup");
                output.WriteLine(host.Output);
                output.WriteLine(await broker.LogsAsync(Token));
            }
        }
    }
}
