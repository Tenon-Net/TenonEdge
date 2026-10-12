using System.Net;
using System.Net.Http.Json;
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
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(3, 1)]
    [InlineData(0, 3)]
    [Trait("Category", "Process")]
    [Trait("Category", "RealBroker")]
    public async Task SharedMqttStartupFaultDoesNotRollBackAndRestartsOnlyFailedInput(int backlogCount, int startupFaultCount)
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
                for (var attempt = 1; attempt <= startupFaultCount; attempt++)
                { File.WriteAllText(Path.Combine(work.Path, PartialActivationControl.AttemptFile(PartialActivationControl.FaultRequestedFile, attempt)), "fail before original loop call"); }
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
                // 多次故障行保留工厂包装，仅放行原激活事件；后续实例仍由原监督者实际释放/退避/重建。
                File.WriteAllText(Path.Combine(work.Path, PartialActivationControl.LifecycleReleasedFile), "release original activation event");
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
                Assert.False(File.Exists(Path.Combine(work.Path, PartialActivationControl.RestartOriginalCalledFile)));
                Assert.DoesNotContain("TEST MQTT STARTUP RETRY RELEASED", host.Output, StringComparison.Ordinal);
                Assert.DoesNotContain("仅测试：原 MQTT 输入循环调用前的启动异常。", degraded.GetRawText(), StringComparison.Ordinal);
                await AssertMqttStopSessionAsync(broker, data.Path, draft.ConnectionId, clientId);
                output.WriteLine("TEST MQTT STARTUP FAULT DEGRADED: original fault disposed; original supervisor backoff/rebuild, only slow restartCount1; retry original loop still not called; r2 completed/committed=true; original HTTP socket + frozen MQTT2/HTTP2 acknowledged unchanged; offline old126/127 unmatched; fast84 delivered to both targets; connections2/subscribed4/sessionLost0; no rollback.");
                for (var attempt = 2; attempt <= startupFaultCount; attempt++)
                {
                    File.WriteAllText(Path.Combine(work.Path, PartialActivationControl.AttemptFile(PartialActivationControl.RestartReleasedFile, attempt - 1)), "inject next startup exception");
                    await WaitForGateAsync(work.Path, PartialActivationControl.AttemptFile(PartialActivationControl.FaultDisposedFile, attempt), "input.mqtt/meter-slow/slow-stop");
                    await WaitForGateAsync(work.Path, PartialActivationControl.AttemptFile(PartialActivationControl.RestartEnteredFile, attempt), "input.mqtt/meter-slow/slow-stop");
                    var next = await api.GetJsonAsync(Api + "/runtime/status", Token);
                    AssertMqttStopRunning(next); // 第四个包装实例 running 不冒充其原循环已调用。
                    Assert.Equal(attempt, MqttStopUnit(next, "meter-slow/slow-stop").GetProperty("restartCount").GetInt32());
                    Assert.Equal(0, MqttStopUnit(next, "meter-slow/slow-stop").GetProperty("reads").GetProperty("readsCompleted").GetInt64());
                    Assert.Equal(0, MqttStopUnit(next, "meter-slow/slow-stop").GetProperty("reads").GetProperty("readsFailed").GetInt64());
                    Assert.Equal(0, MqttStopUnit(next, "meter/sample").GetProperty("restartCount").GetInt32());
                    Assert.Equal(2, next.GetProperty("runtime").GetProperty("activeRevision").GetInt64());
                    var repeated = Assert.Single(state.Events("runtime.unitFaulted"));
                    Assert.Equal("input.mqtt/meter-slow/slow-stop", repeated.Target);
                    Assert.Equal(attempt, repeated.Count);
                    Assert.Contains($"秒后重启（连续第 {attempt} 次）", repeated.Message, StringComparison.Ordinal);
                    Assert.Empty(state.Events("runtime.unitBuildFailed"));
                    Assert.False(File.Exists(Path.Combine(work.Path, PartialActivationControl.AttemptFile(PartialActivationControl.RestartReleasedFile, attempt))));
                    for (var retry = 1; retry <= attempt; retry++)
                    { Assert.False(File.Exists(Path.Combine(work.Path, PartialActivationControl.AttemptFile(PartialActivationControl.RestartOriginalCalledFile, retry)))); }
                    Assert.DoesNotContain("TEST MQTT STARTUP RETRY ORIGINAL CALLED", host.Output, StringComparison.Ordinal);
                    // 故障后下一实例仍在 gate 上，健康输入在同一共享会话继续真实扇出；不重启 Host/输出。
                    var healthyValue = 93 + attempt;
                    await PublishMqttStopAsync(peer, prefix + "/a-new", healthyValue);
                    var healthy = await ReceiveMqttStopValuesAsync(receiver, httpFrozen, [healthyValue], deviceKey: "meter");
                    await ReceiveMqttFanOutSamplesAsync(messages, prefix + "/output", healthy);
                    Assert.NotEqual(SourceMessageId(fast[0]), SourceMessageId(healthy[0]));
                    await WaitForAsync(api, Api + "/runtime/status", _ => ReadDeliveries(data.Path).Length == 0 &&
                        MqttCounter(data.Path, draft.ConnectionId, "acknowledged") == 4 + attempt);
                    await AssertMqttStopSessionAsync(broker, data.Path, draft.ConnectionId, clientId);
                    Assert.Equal(0, MqttCounter(data.Path, draft.ConnectionId, "handoffFailed"));
                    Assert.Equal((2L, (long?)1, (long?)null, (long?)2),
                        (state.Pointers().Effective, state.Pointers().Previous, state.Pointers().Pending, state.Pointers().Running));
                    Assert.Equal(2, state.Snapshots().Count);
                    Assert.Equal(0, state.Snapshot(2).AbnormalExitCount);
                    Assert.Empty(state.Audit(AuditActions.AutoRollback));
                    Assert.Empty(state.Events(RuntimeEventTypes.VersionNotEffective));
                    output.WriteLine($"TEST MQTT STARTUP FAULT REPEATED: failedAttempt={attempt}; next wrapper gated, all original slow loops not called; only slow restartCount{attempt}; merged fault count{repeated.Count}/consecutive{attempt}; fast{healthyValue} delivered to MQTT+HTTP; connections2/subscribed4/acknowledged{4 + attempt}/sessionLost0/handoffFailed0; r2/dirty retained, no new snapshot/rollback/abnormal exit.");
                }
                long[] recoveryValues = backlogCount == 3 ? [86, 87, 88] : [86];
                var expectedAcknowledged = (backlogCount == 3 ? 10 : 8) + startupFaultCount - 1;
                if (backlogCount > 0)
                {
                    Assert.Equal(0, MqttCounter(data.Path, draft.ConnectionId, "handoffFailed"));
                    // 只发布这一次。PUBACK 属于发布方/Broker；停用绑定不得确认或伪造持久接管。
                    await PublishMqttStopAsync(peer, prefix + "/b-new", 86);
                    var blocked = await WaitForAsync(api, Api + "/runtime/status", _ =>
                        MqttCounter(data.Path, draft.ConnectionId, "handoffFailed") >= 1);
                    // 先观察未确认的队头，再积存后续报文；它们也只发布一次，不假定已被投递或 DUP=true。
                    foreach (var value in recoveryValues.Skip(1))
                    { await PublishMqttStopAsync(peer, prefix + "/b-new", (int)value); }
                    blocked = await api.GetJsonAsync(Api + "/runtime/status", Token);
                    Assert.Equal(5, MqttCounter(data.Path, draft.ConnectionId, "acknowledged"));
                    Assert.Equal(0, MqttCounter(data.Path, draft.ConnectionId, "handoffTimeout"));
                    Assert.Equal(0, MqttStopUnit(blocked, "meter-slow/slow-stop").GetProperty("reads").GetProperty("readsCompleted").GetInt64());
                    Assert.Equal(0, MqttStopUnit(blocked, "meter-slow/slow-stop").GetProperty("reads").GetProperty("readsFailed").GetInt64());
                    Assert.Equal(1, MqttStopUnit(blocked, "meter-slow/slow-stop").GetProperty("restartCount").GetInt32());
                    Assert.Equal(0, MqttStopUnit(blocked, "meter/sample").GetProperty("restartCount").GetInt32());
                    Assert.Equal(2, blocked.GetProperty("runtime").GetProperty("activeRevision").GetInt64());
                    Assert.Equal(0, blocked.GetProperty("inFlight").GetProperty("reservedBytes").GetInt64());
                    Assert.Empty(ReadDeliveries(data.Path));
                    Assert.False(receiver.Pending());
                    Assert.Empty(await messages.CollectAsync(TimeSpan.Zero, Token));
                    Assert.False(File.Exists(Path.Combine(work.Path, PartialActivationControl.RestartReleasedFile)));
                    Assert.False(File.Exists(Path.Combine(work.Path, PartialActivationControl.RestartOriginalCalledFile)));
                    output.WriteLine($"TEST MQTT STARTUP FAULT BACKLOG BLOCKED: backlogCount={backlogCount}; slow{string.Join(',', recoveryValues)} published once each before retry original loop, later messages after observed head failure; real handoffFailed>=1, no timeout/ACK/takeover; acknowledged5, slow reads0, no queue/HTTP/MQTT delivery; only slow restartCount1; r2 retained.");
                }
                File.WriteAllText(Path.Combine(work.Path, PartialActivationControl.AttemptFile(PartialActivationControl.RestartReleasedFile, startupFaultCount)), "run rebuilt original input");
                await WaitForGateAsync(work.Path, PartialActivationControl.AttemptFile(PartialActivationControl.RestartOriginalCalledFile, startupFaultCount), "input.mqtt/meter-slow/slow-stop");
                File.Delete(Path.Combine(work.Path, PartialActivationControl.ArmedFile));
                if (backlogCount == 0)
                { await PublishMqttStopAsync(peer, prefix + "/b-new", 86); }
                var recovered = await ReceiveMqttStopValuesAsync(receiver, httpFrozen, recoveryValues);
                await ReceiveMqttFanOutSamplesAsync(messages, prefix + "/output", recovered);
                Assert.Equal(recoveryValues.Length, recovered.Select(SourceMessageId).Distinct(StringComparer.Ordinal).Count());
                Assert.DoesNotContain(recovered, request => SourceMessageId(request) == SourceMessageId(fast[0]));
                for (var index = 0; index < recovered.Length; index++)
                {
                    await AssertMqttStartupSampleAsync(api, draft.SlowInputId, dirtyVersion, SourceMessageId(recovered[index]),
                        index == 0 ? backlogCount > 0 : null);
                }
                output.WriteLine($"TEST MQTT STARTUP FAULT RECOVERY FIFO: backlogCount={backlogCount}; values={string.Join(',', recovered.Select(Value))}; MQTT+HTTP matched in wire order; distinct source IDs={string.Join(',', recovered.Select(SourceMessageId))}; distinct per-message/per-target delivery IDs; no republish after release.");
                await PublishMqttStopAsync(peer, prefix + "/a-old", 252);
                await PublishMqttStopAsync(peer, prefix + "/b-old", 253);
                await PublishMqttStopAsync(peer, prefix + "/a-dirty", 128);
                await PublishMqttStopAsync(peer, prefix + "/b-dirty", 129);
                await PublishMqttStopAsync(peer, prefix + "/a-new", 168);
                await PublishMqttStopAsync(peer, prefix + "/b-new", 169);
                var live = await ReceiveMqttStopValuesAsync(receiver, httpFrozen, [168, 169]);
                await ReceiveMqttFanOutSamplesAsync(messages, prefix + "/output", live);
                var running = await WaitForAsync(api, Api + "/runtime/status", _ => ReadDeliveries(data.Path).Length == 0 &&
                    MqttCounter(data.Path, draft.ConnectionId, "acknowledged") == expectedAcknowledged);
                AssertMqttStopRunning(running);
                Assert.Equal(2, running.GetProperty("runtime").GetProperty("activeRevision").GetInt64());
                Assert.Equal(startupFaultCount, MqttStopUnit(running, "meter-slow/slow-stop").GetProperty("restartCount").GetInt32());
                Assert.All(running.GetProperty("runtime").GetProperty("units").EnumerateArray().Where(unit =>
                    unit.GetProperty("id").GetProperty("key").GetString() != "meter-slow/slow-stop"), unit =>
                    Assert.Equal(0, unit.GetProperty("restartCount").GetInt32()));
                Assert.Equal(2 + startupFaultCount - 1, MqttStopUnit(running, "meter/sample").GetProperty("reads").GetProperty("readsCompleted").GetInt64());
                Assert.Equal(backlogCount == 3 ? 4 : 2, MqttStopUnit(running, "meter-slow/slow-stop").GetProperty("reads").GetProperty("readsCompleted").GetInt64());
                Assert.Equal(2, MqttCounter(data.Path, draft.ConnectionId, "unmatched"));
                var connections = (await broker.LogsAsync(Token)).Split('\n').Count(line => line.Contains($" as {clientId} (", StringComparison.Ordinal));
                var subscriptions = MqttCounter(data.Path, draft.ConnectionId, "subscribed");
                if (backlogCount > 0)
                {
                    // 未接管会中止共享连接，等待期间可多次重连，甚至在订阅过程中再次中止。
                    Assert.True(connections >= 3);
                    Assert.True(subscriptions >= 6);
                    Assert.Equal(0, MqttCounter(data.Path, draft.ConnectionId, "sessionLost"));
                    Assert.DoesNotContain("already connected, closing old connection", await broker.LogsAsync(Token), StringComparison.Ordinal);
                }
                else
                {
                    await AssertMqttStopSessionAsync(broker, data.Path, draft.ConnectionId, clientId);
                    Assert.Equal(0, MqttCounter(data.Path, draft.ConnectionId, "handoffFailed"));
                }
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
                Assert.Equal(startupFaultCount, Assert.Single(state.Events("runtime.unitFaulted")).Count);
                Assert.Empty(await messages.CollectAsync(TimeSpan.Zero, Token));
                await host.RequestStopAsync(Token);
                Assert.Equal(HostExitCodes.Success, await host.WaitForExitAsync(Deadline, Token));
                Assert.Null(state.Pointers().Running);
                Assert.Equal(0, state.Snapshot(1).AbnormalExitCount);
                Assert.Equal(0, state.Snapshot(2).AbnormalExitCount);
                for (var attempt = 1; attempt <= startupFaultCount; attempt++)
                {
                    var injected = host.Output.IndexOf($"TEST MQTT STARTUP FAULT INJECTED: input.mqtt/meter-slow/slow-stop; attempt={attempt};", StringComparison.Ordinal);
                    var disposed = host.Output.IndexOf($"TEST MQTT STARTUP FAULT ORIGINAL DISPOSED: input.mqtt/meter-slow/slow-stop; attempt={attempt}", StringComparison.Ordinal);
                    var built = host.Output.IndexOf($"TEST PARTIAL ACTIVATION INPUT BUILT: input.mqtt/meter-slow/slow-stop; attempt={attempt + 1}", StringComparison.Ordinal);
                    var retry = host.Output.IndexOf($"TEST MQTT STARTUP RETRY ENTERED: input.mqtt/meter-slow/slow-stop; attempt={attempt + 1}", StringComparison.Ordinal);
                    Assert.True(injected >= 0 && injected < disposed && disposed < built && built < retry, host.Output);
                }
                var released = host.Output.IndexOf($"TEST MQTT STARTUP RETRY RELEASED: input.mqtt/meter-slow/slow-stop; attempt={startupFaultCount + 1}", StringComparison.Ordinal);
                var called = host.Output.IndexOf($"TEST MQTT STARTUP RETRY ORIGINAL CALLED: input.mqtt/meter-slow/slow-stop; attempt={startupFaultCount + 1}", StringComparison.Ordinal);
                Assert.True(released >= 0 && released < called, host.Output);
                Assert.Single(host.Output.Split('\n'), line => line.Contains("TEST MQTT STARTUP RETRY ORIGINAL CALLED", StringComparison.Ordinal));
                Assert.DoesNotContain("TEST PARTIAL ACTIVATION LIFECYCLE DEADLINE EXPIRED", host.Output, StringComparison.Ordinal);
                output.WriteLine($"TEST MQTT STARTUP FAULT RECOVERED: backlogCount={backlogCount}; startupFaultCount={startupFaultCount}; same process r2; original supervisor rebuilt only failed slow input after each disposal/backoff; real slow{string.Join(',', recoveryValues)} + fast/slow live168/169 fan out to MQTT+HTTP with shared source IDs/distinct delivery IDs; old/dirty ignored; connections{connections}/subscribed{subscriptions}/acknowledged{expectedAcknowledged}/unmatched2/sessionLost0; no new snapshot/automatic rollback; abnormal counts0; dirty retained; pause false; queue empty; SIGTERM0.");
            }
            finally
            {
                ReleaseMqttPartialActivation(work.Path);
                for (var retry = 1; retry <= startupFaultCount; retry++)
                { File.WriteAllText(Path.Combine(work.Path, PartialActivationControl.AttemptFile(PartialActivationControl.RestartReleasedFile, retry)), "cleanup"); }
                output.WriteLine(host.Output);
                output.WriteLine(await broker.LogsAsync(Token));
            }
        }
    }

    private async Task AssertMqttStartupSampleAsync(HttpClient api, long source, string version, string sourceMessageId, bool? expectedDuplicate)
    {
        var routes = await api.GetJsonAsync(Api + "/routes", Token);
        Assert.Equal(version, routes.GetProperty("version").GetString());
        var route = Assert.Single(routes.GetProperty("routes").EnumerateArray());
        var detail = await api.GetJsonAsync($"{Api}/routes/{route.GetProperty("id").GetInt64()}", Token);
        var graph = detail.GetProperty("route").GetProperty("graph");
        var records = await PostAsync(api, Api + "/routes/preview/recent/messages", new
        { expectedVersion = version, graph, sourceId = source });
        var record = Assert.Single(records.GetProperty("records").EnumerateArray(), item => item.GetProperty("sourceMessageId").GetString() == sourceMessageId);
        Assert.Equal("2", record.GetProperty("revision").GetString());
        Assert.True(record.GetProperty("executable").GetBoolean());
        using var response = await api.PostAsJsonAsync(new Uri(Api + "/routes/preview/recent", UriKind.Relative), new
        {
            expectedVersion = version,
            graph,
            sourceId = source,
            sequence = record.GetProperty("sequence").GetString(),
            revision = record.GetProperty("revision").GetString(),
            sourceMessageId,
        }, Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var sample = await response.ReadJsonAsync(Token);
        Assert.True(sample.GetProperty("previewed").GetBoolean());
        Assert.Empty(sample.GetProperty("issues").EnumerateArray());
        var mqtt = sample.GetProperty("mqtt");
        var duplicate = mqtt.GetProperty("duplicate").GetBoolean();
        if (expectedDuplicate.HasValue)
        { Assert.Equal(expectedDuplicate.Value, duplicate); }
        Assert.False(mqtt.GetProperty("retained").GetBoolean());
        Assert.Equal(1, mqtt.GetProperty("qos").GetInt32());
        output.WriteLine($"TEST MQTT STARTUP FAULT SAMPLE: source={sourceMessageId}; revision2; captured QoS1/retained=false/DUP={duplicate}; authenticated recent preview, no acquisition/output/queue.");
    }
}
