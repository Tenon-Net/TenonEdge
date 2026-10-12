using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using TenonEdge.Host.Bootstrap;
using TenonEdge.Host.Features.Audit;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Storage;
using TenonEdge.IntegrationTests.Infrastructure;

namespace TenonEdge.IntegrationTests.Processes;

public sealed partial class ConfigurationInterruptionProcessTests
{
    [Theory]
    [Trait("Category", "Process")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DurableCommitBeforeSubmitReturnsKeepsNewVersionAndFrozenDelivery(bool interrupt)
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "持久 COMMIT 返回窗口的真实 SIGKILL 恢复在 Linux 验证。");
        using var work = TemporaryDirectory.Create();
        using var data = TemporaryDirectory.Create();
        using var receiver = new TcpListener(IPAddress.Loopback, 0);
        receiver.Start();
        var port = ((IPEndPoint)receiver.LocalEndpoint).Port;
        var state = new RuntimeStateReader(data.Path);
        long deviceId;
        string dirtyVersion;
        string appliedPath;
        DeliveryRow[] frozen;
        await using (var host = HostProcess.StartDurableCommitProbe(work.Path, data.Path, DeploymentArguments(port)))
        {
            var killed = false;
            try
            {
                using var api = await ClientAsync(host, setup: true);
                var draft = await CreateDraftAsync(api, port);
                deviceId = draft.DeviceId;
                var initial = await BeginApplyAsync(api, draft.Version);
                await WaitForAsync(api, initial, operation => operation.GetProperty("stage").GetString() == "completed");
                using var peer = await AcceptAsync(receiver);
                var original = await ReadRequestAsync(peer.GetStream());
                Assert.Equal(42, Value(original)); // 真实发送已开始，但不确认旧队头。
                var version = await EditSampleAsync(api, draft.DeviceId, draft.InputId, draft.Version, 84);
                File.WriteAllText(Path.Combine(work.Path, ConfigurationTransactionGate.CommittedArmedFile), "armed");
                appliedPath = await BeginApplyAsync(api, version);
                var checkpoint = await WaitForDurableCommitGateAsync(work.Path);
                Assert.Equal(Path.Combine(data.Path, ConfigDatabase.FileName), checkpoint.GetProperty("database").GetString());
                Assert.False(checkpoint.GetProperty("inTransaction").GetBoolean());
                Assert.True(checkpoint.GetProperty("originalConnectionClosed").GetBoolean());
                Assert.Equal(2, checkpoint.GetProperty("effective").GetInt64());
                Assert.Equal(1, checkpoint.GetProperty("previous").GetInt64());
                Assert.Equal(JsonValueKind.Null, checkpoint.GetProperty("pending").ValueKind);
                Assert.Equal(2, checkpoint.GetProperty("running").GetInt64());
                Assert.Equal(RuntimeSnapshotStates.History, checkpoint.GetProperty("oldState").GetString());
                Assert.Equal(RuntimeSnapshotStates.Effective, checkpoint.GetProperty("newState").GetString());
                Assert.Equal(1, checkpoint.GetProperty("appliedAuditCount").GetInt64());
                Assert.Equal("wal", checkpoint.GetProperty("journalMode").GetString());
                Assert.Equal(2, checkpoint.GetProperty("synchronous").GetInt64());
                Assert.Equal(5000, checkpoint.GetProperty("busyTimeout").GetInt64());
                var operation = await api.GetJsonAsync(appliedPath, Token);
                Assert.Equal("committing", operation.GetProperty("stage").GetString());
                Assert.Equal(JsonValueKind.Null, operation.GetProperty("committed").ValueKind);
                Assert.Equal(2, operation.GetProperty("revision").GetInt64());
                var quiet = await api.GetJsonAsync(Api + "/runtime/status", Token);
                Assert.Equal(1, quiet.GetProperty("runtime").GetProperty("activeRevision").GetInt64());
                Assert.Equal("stopped", Input(quiet).GetProperty("state").GetString());
                Assert.Equal("stopped", Assert.Single(quiet.GetProperty("runtime").GetProperty("units").EnumerateArray(),
                    unit => unit.GetProperty("id").GetProperty("kind").GetString() == "delivery.dispatcher").GetProperty("state").GetString());
                Assert.Equal("running", Assert.Single(quiet.GetProperty("runtime").GetProperty("units").EnumerateArray(),
                    unit => unit.GetProperty("id").GetProperty("kind").GetString() == "output.http").GetProperty("state").GetString());
                Assert.Equal(0, quiet.GetProperty("inFlight").GetProperty("reservedBytes").GetInt64());
                Assert.Equal((2L, (long?)1, (long?)null, (long?)2),
                    (state.Pointers().Effective, state.Pointers().Previous, state.Pointers().Pending, state.Pointers().Running));
                frozen = ReadDeliveries(data.Path);
                Assert.NotEmpty(frozen);
                Assert.Equal(original.Body, Encoding.UTF8.GetBytes(frozen[0].Payload));
                Assert.Equal(frozen[0].SourceId, SourceMessageId(original));
                Assert.Contains($"Idempotency-Key: {frozen[0].Id}", original.Header, StringComparison.OrdinalIgnoreCase);
                Assert.All(frozen, row =>
                {
                    Assert.Equal(1, row.Revision);
                    using var document = JsonDocument.Parse(row.Payload);
                    Assert.Equal(42, document.RootElement.GetProperty("values").GetProperty("value").GetInt64());
                });
                Assert.False(receiver.Pending());
                // COMMIT 已释放写锁：上层提交尚未返回，另一个认证请求仍可保存下一轮草稿。
                dirtyVersion = await EditSampleAsync(api, draft.DeviceId, draft.InputId, version, 126);
                await AssertDurableCommitDirtyAsync(api, deviceId, dirtyVersion);
                Assert.Equal(["r1", "r2"], state.Audit(AuditActions.SnapshotApplied).Select(entry => entry.Target).ToArray());
                Assert.False(File.Exists(Path.Combine(work.Path, ConfigurationTransactionGate.CommittedReleasedFile)));
                output.WriteLine($"TEST DURABLE COMMIT CHECKPOINT: committing/committed=null; runtime active r1; old input/dispatcher stopped; persistent effective/running=r2, previous=r1, pending empty; r2 audit committed; no r2 delivery; dirty126 saved; frozen={frozen.Length}.");
                if (!interrupt)
                {
                    File.Delete(Path.Combine(work.Path, ConfigurationTransactionGate.CommittedArmedFile));
                    File.WriteAllText(Path.Combine(work.Path, ConfigurationTransactionGate.CommittedReleasedFile), "normal release");
                    var completed = await WaitForAsync(api, appliedPath, status => status.GetProperty("stage").GetString() == "completed");
                    Assert.True(completed.GetProperty("committed").GetBoolean());
                    Assert.Equal(2, completed.GetProperty("revision").GetInt64());
                    var active = await api.GetJsonAsync(Api + "/runtime/status", Token);
                    Assert.Equal(2, active.GetProperty("runtime").GetProperty("activeRevision").GetInt64());
                    Assert.Equal("running", Input(active).GetProperty("state").GetString());
                    await AckAsync(peer.GetStream()); // 原 HTTP 在途连接保持可用，不靠重启打断。
                    await ReplayDurableCommitFrozenAsync(receiver, frozen.Skip(1));
                    await ReceiveDurableCommitFreshAsync(receiver, frozen);
                    await WaitForAsync(api, Api + "/runtime/status", _ => ReadDeliveries(data.Path).Length == 0);
                    await AssertDurableCommitDirtyAsync(api, deviceId, dirtyVersion);
                    Assert.Equal(2, state.Snapshots().Count);
                    Assert.Equal(0, state.Snapshot(1).AbnormalExitCount);
                    Assert.Equal(0, state.Snapshot(2).AbnormalExitCount);
                    Assert.Equal(["r1", "r2"], state.Audit(AuditActions.SnapshotApplied).Select(entry => entry.Target).ToArray());
                    Assert.Empty(state.Audit(AuditActions.AutoRollback));
                    Assert.Empty(state.Events(RuntimeEventTypes.VersionNotEffective));
                    await host.RequestStopAsync(Token);
                    Assert.Equal(HostExitCodes.Success, await host.WaitForExitAsync(Deadline, Token));
                    Assert.Null(state.Pointers().Running);
                    Assert.Contains("TEST DURABLE COMMIT GATE RELEASED", host.Output, StringComparison.Ordinal);
                    output.WriteLine("TEST DURABLE COMMIT RELEASED: original COMMIT/upper submit returned; r2 completed/running; original HTTP socket acknowledged; frozen r1/42 unchanged, fresh84 has new sourceMessageId; dirty126 preserved; no new version/automatic rollback; abnormal counts0; queue empty; SIGTERM0.");
                    return;
                }
                using var queue = OpenQueue(data.Path);
                using var held = queue.BeginTransaction(deferred: false);
                Assert.Equal(frozen, ReadDeliveries(data.Path));
                await host.KillAsync(Token);
                killed = true;
                held.Rollback();
                Assert.Equal(frozen, ReadDeliveries(data.Path));
                Assert.DoesNotContain("TEST DURABLE COMMIT GATE RELEASED", host.Output, StringComparison.Ordinal);
            }
            finally
            {
                if (!killed && !File.Exists(Path.Combine(work.Path, ConfigurationTransactionGate.CommittedReleasedFile)))
                { File.WriteAllText(Path.Combine(work.Path, ConfigurationTransactionGate.CommittedReleasedFile), "test cleanup"); }
                output.WriteLine(host.Output);
            }
        }

        await using var restarted = Start(work.Path, data.Path, port); // 未经注入的产品 Host。
        try
        {
            using var api = await ClientAsync(restarted, setup: false);
            Assert.Equal((2L, (long?)1, (long?)null, (long?)2),
                (state.Pointers().Effective, state.Pointers().Previous, state.Pointers().Pending, state.Pointers().Running));
            Assert.Equal(2, state.Snapshots().Count);
            Assert.Equal(0, state.Snapshot(1).AbnormalExitCount);
            Assert.Equal(1, state.Snapshot(2).AbnormalExitCount);
            Assert.Equal(RuntimeSnapshotStates.History, state.Snapshot(1).State);
            Assert.Equal(RuntimeSnapshotStates.Effective, state.Snapshot(2).State);
            Assert.Null(state.Snapshot(2).NotEffectiveReason);
            Assert.Equal(["r1", "r2"], state.Audit(AuditActions.SnapshotApplied).Select(entry => entry.Target).ToArray());
            Assert.Empty(state.Audit(AuditActions.AutoRollback));
            Assert.Empty(state.Events(RuntimeEventTypes.VersionNotEffective));
            await AssertDurableCommitDirtyAsync(api, deviceId, dirtyVersion);
            Assert.Empty((await api.GetJsonAsync(Api + "/configuration/operations/", Token)).EnumerateArray());
            using var missing = await api.GetAsync(new Uri(appliedPath, UriKind.Relative), Token);
            await ProblemAssert.HasStatusAsync(missing, HttpStatusCode.NotFound, Token);
            await ReplayDurableCommitFrozenAsync(receiver, frozen);
            await ReceiveDurableCommitFreshAsync(receiver, frozen);
            await WaitForAsync(api, Api + "/runtime/status", status =>
                status.GetProperty("runtime").GetProperty("activeRevision").GetInt64() == 2 &&
                Input(status).GetProperty("state").GetString() == "running" && ReadDeliveries(data.Path).Length == 0);
            await restarted.RequestStopAsync(Token);
            Assert.Equal(HostExitCodes.Success, await restarted.WaitForExitAsync(Deadline, Token));
            Assert.Null(state.Pointers().Running);
            output.WriteLine("TEST DURABLE COMMIT RECOVERED: original product Host loads r2; frozen r1/42 FIFO replayed unchanged; fresh84 has new sourceMessageId; dirty126 preserved; r2 abnormal count1, r1 count0; no new version/automatic rollback/notEffective; queue empty; SIGTERM0.");
        }
        finally { output.WriteLine(restarted.Output); }
    }

    private static async Task AssertDurableCommitDirtyAsync(HttpClient api, long deviceId, string version)
    {
        var sources = await api.GetJsonAsync($"{Api}/devices/{deviceId}/inputs", Token);
        Assert.Equal(version, sources.GetProperty("version").GetString());
        Assert.Equal("{\"value\":126}", sources.GetProperty("inputs")[0].GetProperty("simulator").GetProperty("sampleJson").GetString());
    }

    private static async Task ReplayDurableCommitFrozenAsync(TcpListener receiver, IEnumerable<DeliveryRow> frozen)
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

    private static async Task ReceiveDurableCommitFreshAsync(TcpListener receiver, DeliveryRow[] frozen)
    {
        using var collected = await AcceptAsync(receiver);
        var request = await ReadRequestAsync(collected.GetStream());
        Assert.Equal(84, Value(request)); // 既不是 r1/42，也不是未应用草稿126。
        Assert.DoesNotContain(frozen, row => row.SourceId == SourceMessageId(request));
        await AckAsync(collected.GetStream());
    }

    private static async Task<JsonElement> WaitForDurableCommitGateAsync(string directory)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
        deadline.CancelAfter(Deadline);
        var path = Path.Combine(directory, ConfigurationTransactionGate.CommittedEnteredFile);
        while (!File.Exists(path))
        { await Task.Delay(10, deadline.Token); }
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path, deadline.Token));
        return document.RootElement.Clone();
    }
}
