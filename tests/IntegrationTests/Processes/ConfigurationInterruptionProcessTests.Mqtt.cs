using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using MQTTnet;
using MQTTnet.Protocol;
using TenonEdge.Host.Bootstrap;
using TenonEdge.Host.Features.Audit;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Storage;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.IntegrationTests.Probes.Mqtt;

namespace TenonEdge.IntegrationTests.Processes;

public sealed partial class ConfigurationInterruptionProcessTests
{
    private const string MqttStopArmed = MqttPartialStopControl.ArmedFile;
    private const string MqttStopEntered = MqttPartialStopControl.EnteredFile;
    private const string MqttStopReleased = MqttPartialStopControl.ReleasedFile;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [Trait("Category", "Process")]
    [Trait("Category", "RealBroker")]
    public async Task SharedMqttPartialStopKeepsVersionAndDeliveryBoundaries(bool interrupt)
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "共享 MQTT 部分停止与 SIGKILL 恢复只在 Linux 验证。");
        await using var broker = await MosquittoBrokerFixture.StartDedicatedAsync(Token);
        output.WriteLine($"TEST MQTT PARTIAL STOP BROKER PORTS: {broker.Port},{broker.TlsPort}");
        using var work = TemporaryDirectory.Create();
        using var data = TemporaryDirectory.Create();
        using var receiver = new TcpListener(IPAddress.Loopback, 0);
        receiver.Start();
        var port = ((IPEndPoint)receiver.LocalEndpoint).Port;
        var prefix = MqttProbe.NewTopic("partial-stop");
        var clientId = "partial-stop-" + Guid.NewGuid().ToString("N");
        var (publisher, _) = await MqttProbe.ConnectAsync(MqttProbe.Options(broker, "publisher-" + Guid.NewGuid().ToString("N")), Token);
        using var peer = publisher;
        var state = new RuntimeStateReader(data.Path);
        MqttStopDraft draft;
        string applied, dirtyVersion;
        DeliveryRow[] frozen;
        await using (var host = HostProcess.StartMqttPartialStopProbe(work.Path, data.Path, MqttStopArguments(port, broker.Port)))
        {
            try
            {
                using var api = await ClientAsync(host, setup: true);
                draft = await CreateMqttStopDraftAsync(api, port, broker, clientId, prefix);
                var initial = await BeginApplyAsync(api, draft.Version);
                await WaitForAsync(api, initial, operation => operation.GetProperty("stage").GetString() == "completed");
                await WaitForAsync(api, Api + "/runtime/status", _ => MqttCounter(data.Path, draft.ConnectionId, "subscribed") == 2);
                Assert.Single((await broker.LogsAsync(Token)).Split('\n'), line => line.Contains($" as {clientId} (", StringComparison.Ordinal));
                await PublishMqttStopAsync(peer, prefix + "/a-old", 42);
                using var accepted = await AcceptAsync(receiver);
                var original = await ReadRequestAsync(accepted.GetStream()); // 真实 HTTP 队头保持未确认。
                Assert.Equal(42, Value(original));
                await PublishMqttStopAsync(peer, prefix + "/b-old", 43);
                await WaitForAsync(api, Api + "/runtime/status", _ => ReadDeliveries(data.Path).Length == 2 &&
                    MqttCounter(data.Path, draft.ConnectionId, "acknowledged") == 2);
                var version = await EditMqttStopSourceAsync(api, draft.DeviceId, draft.InputId, draft.Version, "sample", prefix + "/a-new");
                version = await EditMqttStopSourceAsync(api, draft.SlowDeviceId, draft.SlowInputId, version, "slow-stop", prefix + "/b-new");
                File.WriteAllText(Path.Combine(work.Path, MqttStopArmed), "armed");
                applied = await BeginApplyAsync(api, version);
                await WaitForGateAsync(work.Path, MqttStopEntered, "input.mqtt/meter-slow/slow-stop");
                var quiet = await WaitForAsync(api, Api + "/runtime/status", status =>
                    MqttStopUnit(status, "meter/sample").GetProperty("state").GetString() == "stopped" &&
                    MqttStopUnit(status, "meter-slow/slow-stop").GetProperty("state").GetString() == "stopping");
                var progress = await api.GetJsonAsync(applied, Token);
                Assert.Equal("quiescing", progress.GetProperty("stage").GetString());
                Assert.False(progress.GetProperty("committed").GetBoolean());
                Assert.Equal(1, quiet.GetProperty("runtime").GetProperty("activeRevision").GetInt64());
                Assert.Equal(0, quiet.GetProperty("inFlight").GetProperty("reservedBytes").GetInt64());
                Assert.Equal((1L, (long?)null, (long?)2, (long?)1),
                    (state.Pointers().Effective, state.Pointers().Previous, state.Pointers().Pending, state.Pointers().Running));
                Assert.Equal(RuntimeSnapshotStates.Pending, state.Snapshot(2).State);
                Assert.False(File.Exists(Path.Combine(work.Path, MqttStopReleased)));
                Assert.DoesNotContain("TEST MQTT PARTIAL STOP ORIGINAL STOPPED", host.Output, StringComparison.Ordinal);
                var dispatchers = quiet.GetProperty("runtime").GetProperty("units").EnumerateArray().Where(unit =>
                    unit.GetProperty("id").GetProperty("kind").GetString() == "delivery.dispatcher").ToArray();
                Assert.Equal(2, dispatchers.Length);
                Assert.All(dispatchers, unit => Assert.Equal("running", unit.GetProperty("state").GetString()));
                await WaitForMqttStopDisconnectAsync(broker, clientId);
                dirtyVersion = await EditMqttStopSourceAsync(api, draft.DeviceId, draft.InputId, version, "sample", prefix + "/a-dirty");
                dirtyVersion = await EditMqttStopSourceAsync(api, draft.SlowDeviceId, draft.SlowInputId, dirtyVersion, "slow-stop", prefix + "/b-dirty");
                output.WriteLine($"共享 MQTT 部分停止窗口：sample stopped/slow-stop stopping，原慢循环未取消/退出；quiescing/committed=false，旧分发器2项 running、在途0；{state.Pointers()}，dirty 草稿保留未应用。");
                if (!interrupt)
                {
                    frozen = ReadDeliveries(data.Path);
                    AssertMqttStopFrozen(frozen, original);
                    File.Delete(Path.Combine(work.Path, MqttStopArmed));
                    File.WriteAllText(Path.Combine(work.Path, MqttStopReleased), "released");
                    var completed = await WaitForAsync(api, applied, operation => operation.GetProperty("stage").GetString() == "completed");
                    Assert.True(completed.GetProperty("committed").GetBoolean());
                    Assert.Equal((2L, (long?)1, (long?)null, (long?)2),
                        (state.Pointers().Effective, state.Pointers().Previous, state.Pointers().Pending, state.Pointers().Running));
                    Assert.Equal(RuntimeSnapshotStates.Effective, state.Snapshot(2).State);
                    await AckAsync(accepted.GetStream());
                    await ReplayMqttStopFrozenAsync(receiver, frozen.Skip(1));
                    await WaitForAsync(api, Api + "/runtime/status", _ => MqttCounter(data.Path, draft.ConnectionId, "subscribed") == 4);
                    await PublishMqttStopAsync(peer, prefix + "/a-new", 84);
                    await PublishMqttStopAsync(peer, prefix + "/b-new", 86);
                    await ReceiveMqttStopValuesAsync(receiver, frozen, [84, 86]);
                    await PublishMqttStopAsync(peer, prefix + "/a-old", 126);
                    await PublishMqttStopAsync(peer, prefix + "/b-old", 127);
                    await PublishMqttStopAsync(peer, prefix + "/a-dirty", 128);
                    await PublishMqttStopAsync(peer, prefix + "/b-dirty", 129);
                    await PublishMqttStopAsync(peer, prefix + "/a-new", 168);
                    await PublishMqttStopAsync(peer, prefix + "/b-new", 169);
                    await ReceiveMqttStopValuesAsync(receiver, frozen, [168, 169]);
                    var running = await WaitForAsync(api, Api + "/runtime/status", _ => ReadDeliveries(data.Path).Length == 0 &&
                        MqttCounter(data.Path, draft.ConnectionId, "acknowledged") == 6);
                    AssertMqttStopRunning(running);
                    await AssertMqttStopSessionAsync(broker, data.Path, draft.ConnectionId, clientId);
                    await AssertMqttStopDirtyAsync(api, draft, prefix, dirtyVersion);
                    Assert.Equal(["r1", "r2"], state.Audit(AuditActions.SnapshotApplied).Select(entry => entry.Target).ToArray());
                    Assert.Empty(state.Audit(AuditActions.AutoRollback));
                    await host.RequestStopAsync(Token);
                    Assert.Equal(HostExitCodes.Success, await host.WaitForExitAsync(Deadline, Token));
                    Assert.Null(state.Pointers().Running);
                    Assert.Equal(0, state.Snapshot(1).AbnormalExitCount);
                    Assert.Equal(0, state.Snapshot(2).AbnormalExitCount);
                    Assert.Equal(2, state.Snapshots().Count);
                    Assert.DoesNotContain(state.Snapshots(), snapshot => snapshot.State == RuntimeSnapshotStates.NotEffective);
                    Assert.Contains("TEST MQTT PARTIAL STOP GATE RELEASED: input.mqtt/meter-slow/slow-stop", host.Output, StringComparison.Ordinal);
                    Assert.Contains("TEST MQTT PARTIAL STOP ORIGINAL STOPPED: input.mqtt/meter-slow/slow-stop", host.Output, StringComparison.Ordinal);
                    Assert.DoesNotContain("在 00:00:05 内没有停止", host.Output, StringComparison.Ordinal);
                    output.WriteLine("共享 MQTT 部分停止正常释放：r2 completed，r1 冻结记录原样确认，原慢循环真实停止并由 r2 两路运行；新主题84/86及实时168/169收到，旧/dirty主题不采集；同一 ClientId 两次连接、sessionLost=0；SIGTERM=0、运行标记清除、异常次数=0。");
                    return;
                }
                // 第一输入的真实停止已经让整个接收器静默并断开；慢输入的原循环/租约尚未退出。
                // 发布者的 PUBACK 只证明 Broker 接收，网关未接管，靠仍存活的持久会话留到重连。
                await PublishMqttStopAsync(peer, prefix + "/a-old", 84);
                await PublishMqttStopAsync(peer, prefix + "/b-old", 86);
                Assert.Equal(2, MqttCounter(data.Path, draft.ConnectionId, "acknowledged"));
                using var queue = OpenQueue(data.Path);
                using var held = queue.BeginTransaction(deferred: false);
                frozen = ReadDeliveries(data.Path);
                AssertMqttStopFrozen(frozen, original);
                Assert.Equal(["r1"], state.Audit(AuditActions.SnapshotApplied).Select(entry => entry.Target).ToArray());
                output.WriteLine($"共享 MQTT 部分停止：两路同一真实 ClientId，sample stopped/slow-stop stopping，原慢循环未取消/退出；r1 effective/r2 pending，旧确认=2，84/86 留在存活 Broker，草稿 dirty 未应用；{state.Pointers()}。");
                await host.KillAsync(Token);
                held.Rollback();
                Assert.Equal(frozen, ReadDeliveries(data.Path));
                Assert.DoesNotContain("TEST MQTT PARTIAL STOP GATE RELEASED", host.Output, StringComparison.Ordinal);
                Assert.DoesNotContain("TEST MQTT PARTIAL STOP ORIGINAL STOPPED", host.Output, StringComparison.Ordinal);
                Assert.DoesNotContain("在 00:00:05 内没有停止", host.Output, StringComparison.Ordinal);
            }
            finally
            {
                output.WriteLine(host.Output);
                output.WriteLine(await broker.LogsAsync(Token));
            }
        }
        await using var restarted = HostProcess.Start(work.Path, "Production", data.Path, MqttStopArguments(port, broker.Port));
        try
        {
            using var api = await ClientAsync(restarted, setup: false);
            Assert.Equal((1L, (long?)null, (long?)null, (long?)1),
                (state.Pointers().Effective, state.Pointers().Previous, state.Pointers().Pending, state.Pointers().Running));
            Assert.Equal(RuntimeSnapshotStates.NotEffective, state.Snapshot(2).State);
            Assert.Equal("进程中断", state.Snapshot(2).NotEffectiveReason);
            Assert.Equal(1, state.Snapshot(1).AbnormalExitCount);
            Assert.Equal(0, state.Snapshot(2).AbnormalExitCount);
            Assert.Equal(2, state.Snapshots().Count);
            Assert.Empty(state.Audit(AuditActions.AutoRollback));
            using var missing = await api.GetAsync(new Uri(applied, UriKind.Relative), Token);
            await ProblemAssert.HasStatusAsync(missing, HttpStatusCode.NotFound, Token);
            await AssertMqttStopDirtyAsync(api, draft, prefix, dirtyVersion);
            await ReplayMqttStopFrozenAsync(receiver, frozen);
            await ReceiveMqttStopValuesAsync(receiver, frozen, [84, 86]);
            await WaitForAsync(api, Api + "/runtime/status", _ => MqttCounter(data.Path, draft.ConnectionId, "acknowledged") == 4);
            await PublishMqttStopAsync(peer, prefix + "/a-new", 126);
            await PublishMqttStopAsync(peer, prefix + "/b-new", 127);
            await PublishMqttStopAsync(peer, prefix + "/a-dirty", 128);
            await PublishMqttStopAsync(peer, prefix + "/b-dirty", 129);
            await PublishMqttStopAsync(peer, prefix + "/a-old", 168);
            await PublishMqttStopAsync(peer, prefix + "/b-old", 169);
            await ReceiveMqttStopValuesAsync(receiver, frozen, [168, 169]);
            var running = await WaitForAsync(api, Api + "/runtime/status", _ => ReadDeliveries(data.Path).Length == 0 &&
                MqttCounter(data.Path, draft.ConnectionId, "acknowledged") == 6);
            AssertMqttStopRunning(running);
            await AssertMqttStopSessionAsync(broker, data.Path, draft.ConnectionId, clientId);
            output.WriteLine("共享 MQTT 部分停止恢复：原 r1 两项冻结记录原样 FIFO 补发，Broker 离线消息84/86及旧主题实时168/169真实收到；pending r2 废弃，dirty 草稿保留，新/dirty 主题不采集；同一 ClientId 仅初始与重启两次连接，sessionLost=0。");
            await restarted.RequestStopAsync(Token);
            Assert.Equal(HostExitCodes.Success, await restarted.WaitForExitAsync(Deadline, Token));
            Assert.Null(state.Pointers().Running);
        }
        finally { output.WriteLine(restarted.Output); }
    }

    private static void AssertMqttStopFrozen(DeliveryRow[] frozen, WireRequest original)
    {
        Assert.Equal(2, frozen.Length);
        Assert.All(frozen, row => Assert.Equal(1, row.Revision));
        Assert.Equal(2, frozen.Select(row => row.SourceId).Distinct(StringComparer.Ordinal).Count());
        for (var index = 0; index < frozen.Length; index++)
        {
            using var json = JsonDocument.Parse(frozen[index].Payload);
            Assert.Equal(index == 0 ? 42 : 43, json.RootElement.GetProperty("values").GetProperty("value").GetInt64());
            Assert.Equal(index == 0 ? "meter" : "meter-slow", json.RootElement.GetProperty("deviceKey").GetString());
            Assert.Equal(frozen[index].SourceId, json.RootElement.GetProperty("sourceMessageId").GetString());
        }
        Assert.Equal(original.Body, Encoding.UTF8.GetBytes(frozen[0].Payload));
        Assert.Equal(frozen[0].SourceId, SourceMessageId(original));
        Assert.Contains($"Idempotency-Key: {frozen[0].Id}", original.Header, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task ReplayMqttStopFrozenAsync(TcpListener receiver, IEnumerable<DeliveryRow> frozen)
    {
        foreach (var row in frozen)
        {
            using var replay = await AcceptAsync(receiver);
            var request = await ReadRequestAsync(replay.GetStream());
            Assert.Equal(Encoding.UTF8.GetBytes(row.Payload), request.Body);
            Assert.Equal(row.SourceId, SourceMessageId(request));
            Assert.Contains($"Idempotency-Key: {row.Id}", request.Header, StringComparison.OrdinalIgnoreCase);
            await AckAsync(replay.GetStream());
        }
    }

    private static void AssertMqttStopRunning(JsonElement running)
    {
        Assert.Equal("running", MqttStopUnit(running, "meter/sample").GetProperty("state").GetString());
        Assert.Equal("running", MqttStopUnit(running, "meter-slow/slow-stop").GetProperty("state").GetString());
    }

    private static async Task AssertMqttStopDirtyAsync(HttpClient api, MqttStopDraft draft, string prefix, string version)
    {
        foreach (var (device, topic) in new[] { (draft.DeviceId, prefix + "/a-dirty"), (draft.SlowDeviceId, prefix + "/b-dirty") })
        {
            var sources = await api.GetJsonAsync($"{Api}/devices/{device}/inputs", Token);
            Assert.Equal(version, sources.GetProperty("version").GetString());
            Assert.Equal(topic, sources.GetProperty("inputs")[0].GetProperty("mqtt").GetProperty("topicFilter").GetString());
        }
    }

    private async Task AssertMqttStopSessionAsync(MosquittoBroker broker, string data, long connection, string clientId)
    {
        Assert.Equal(0, MqttCounter(data, connection, "sessionLost"));
        Assert.Equal(4, MqttCounter(data, connection, "subscribed"));
        var logs = await broker.LogsAsync(Token);
        Assert.Equal(2, logs.Split('\n').Count(line => line.Contains($" as {clientId} (", StringComparison.Ordinal)));
        Assert.DoesNotContain("already connected, closing old connection", logs, StringComparison.Ordinal);
        output.WriteLine($"TEST MQTT PARTIAL STOP SESSION VERIFIED: {clientId}; connections=2; subscribed=4; sessionLost=0");
    }

    private static string[] MqttStopArguments(int httpPort, int mqttPort) =>
        [.. DeploymentArguments(httpPort), "--TenonEdge:Outbound:LocalPorts:1", mqttPort.ToString(CultureInfo.InvariantCulture)];

    private static JsonElement MqttStopUnit(JsonElement status, string key) => Assert.Single(
        status.GetProperty("runtime").GetProperty("units").EnumerateArray(), unit =>
            unit.GetProperty("id").GetProperty("kind").GetString() == "input.mqtt" && unit.GetProperty("id").GetProperty("key").GetString() == key);

    private static long MqttCounter(string data, long connection, string name)
    {
        using var database = OpenQueue(data);
        using var command = database.CreateCommand();
        command.CommandText = "SELECT COALESCE((SELECT value FROM queue_counters WHERE name=$name),0)";
        command.Parameters.AddWithValue("$name", $"connection/{connection}/mqtt/{name}");
        return (long)command.ExecuteScalar()!;
    }

    private static async Task PublishMqttStopAsync(IMqttClient publisher, string topic, int value)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
        deadline.CancelAfter(Deadline);
        var result = await publisher.PublishAsync(new MqttApplicationMessageBuilder().WithTopic(topic)
            .WithPayload(JsonSerializer.Serialize(new { v = value })).WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).Build(), deadline.Token);
        Assert.True(result.IsSuccess);
    }

    private async Task WaitForMqttStopDisconnectAsync(MosquittoBroker broker, string clientId)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
        deadline.CancelAfter(Deadline);
        while (true)
        {
            var logs = await broker.LogsAsync(deadline.Token);
            // 锁定的 Mosquitto 2.1.2 带 [peer] 与关闭原因，Reset/Dispose 不发送 MQTT DISCONNECT。
            var close = logs.Split('\n').FirstOrDefault(line =>
                line.Contains($"Client {clientId} [", StringComparison.Ordinal) &&
                line.EndsWith("] disconnected: connection closed by client.", StringComparison.Ordinal));
            if (close is not null)
            {
                output.WriteLine($"TEST MQTT PARTIAL STOP BROKER CLOSE: {close}");
                return;
            }
            await Task.Delay(25, deadline.Token);
        }
    }

    private static async Task ReceiveMqttStopValuesAsync(TcpListener receiver, DeliveryRow[] frozen, long[] values)
    {
        foreach (var value in values)
        {
            using var peer = await AcceptAsync(receiver);
            var request = await ReadRequestAsync(peer.GetStream());
            Assert.Equal(value, Value(request));
            Assert.DoesNotContain(frozen, row => row.SourceId == SourceMessageId(request));
            using var json = JsonDocument.Parse(request.Body);
            Assert.Equal(value is 84 or 168 ? "meter" : "meter-slow", json.RootElement.GetProperty("deviceKey").GetString());
            await AckAsync(peer.GetStream());
        }
    }

    private static async Task<string> EditMqttStopSourceAsync(HttpClient api, long device, long source, string version, string key, string topic)
    {
        using var edit = await api.PutAsJsonAsync(new Uri($"{Api}/devices/{device}/inputs/{source}", UriKind.Relative), MqttStopSource(version, key, topic), Token);
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        return (await edit.ReadJsonAsync(Token)).GetProperty("version").GetString()!;
    }

    private static object MqttStopSource(string version, string key, string topic) => new
    { expectedVersion = version, key, name = key, mqtt = new { topicFilter = topic, fields = new[] { new { key = "value", path = "/v", type = "integer" } } } };

    private static async Task<MqttStopDraft> CreateMqttStopDraftAsync(HttpClient api, int port, MosquittoBroker broker, string clientId, string prefix)
    {
        var connection = await PostAsync(api, Api + "/connections", new
        { expectedVersion = "0", key = "mqtt", name = "MQTT", kind = "mqtt", mqtt = new { host = "127.0.0.1", port = broker.Port, clientId } });
        var connectionId = connection.GetProperty("connection").GetProperty("id").GetInt64();
        using var credential = await api.PutAsJsonAsync(new Uri($"{Api}/connections/{connectionId}/mqtt-credential", UriKind.Relative), new
        { expectedDraftVersion = connection.GetProperty("version").GetString(), recipient = $"mqtt://127.0.0.1:{broker.Port}", username = MosquittoBroker.UserName, password = MosquittoBroker.Password }, Token);
        Assert.Equal(HttpStatusCode.OK, credential.StatusCode);
        var version = connection.GetProperty("version").GetString()!;
        var devices = new List<(long Device, long Input)>();
        foreach (var (key, inputKey, topic) in new[] { ("meter", "sample", prefix + "/a-old"), ("meter-slow", "slow-stop", prefix + "/b-old") })
        {
            var device = await PostAsync(api, Api + "/devices", new { expectedVersion = version, key, name = key, kind = "mqtt", connectionId });
            var id = device.GetProperty("device").GetProperty("id").GetInt64();
            var source = await PostAsync(api, $"{Api}/devices/{id}/inputs", MqttStopSource(device.GetProperty("version").GetString()!, inputKey, topic));
            devices.Add((id, source.GetProperty("input").GetProperty("id").GetInt64()));
            version = source.GetProperty("version").GetString()!;
        }
        var http = await PostAsync(api, Api + "/connections", new
        { expectedVersion = version, key = "http", name = "HTTP", kind = "http", http = new { baseAddress = $"http://127.0.0.1:{port}/api/", timeoutMs = 60000 } });
        var route = await PostAsync(api, Api + "/routes", new
        {
            expectedVersion = http.GetProperty("version").GetString(),
            key = "route",
            name = "Route",
            enabled = true,
            graph = new
            {
                schemaVersion = 1,
                nodes = new object[]
                {
                    new { id = "in", type = "input.mqtt", version = 1, name = "Input", config = new { sourceIds = devices.Select(device => device.Input).ToArray() } },
                    new { id = "out", type = "output.http", version = 1, name = "Output", config = new { connectionId = http.GetProperty("connection").GetProperty("id").GetInt64() } },
                },
                edges = new[] { new { source = "in", target = "out" } },
                layout = new { nodes = new Dictionary<string, object>() },
            },
        });
        return new(connectionId, devices[0].Device, devices[0].Input, devices[1].Device, devices[1].Input, route.GetProperty("version").GetString()!);
    }

    private sealed record MqttStopDraft(long ConnectionId, long DeviceId, long InputId, long SlowDeviceId, long SlowInputId, string Version);
}
