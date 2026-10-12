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
    [InlineData(true)]
    [InlineData(false)]
    [Trait("Category", "Process")]
    public async Task DurableRollbackCommitRestoresDraftAndKeepsFrozenDeliveries(bool interrupt)
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "人工回滚持久提交窗口的真实 SIGKILL 恢复在 Linux 验证。");
        using var work = TemporaryDirectory.Create();
        using var data = TemporaryDirectory.Create();
        using var receiver = new TcpListener(IPAddress.Loopback, 0);
        receiver.Start();
        var port = ((IPEndPoint)receiver.LocalEndpoint).Port;
        output.WriteLine($"TEST DURABLE ROLLBACK HTTP PORT: {port}");
        var state = new RuntimeStateReader(data.Path);
        long deviceId;
        string dirtyVersion;
        string rollbackPath;
        DeliveryRow[] frozen;
        await using (var host = HostProcess.StartDurableCommitProbe(work.Path, data.Path, DeploymentArguments(port)))
        {
            var killed = false;
            try
            {
                using var api = await ClientAsync(host, setup: true);
                var draft = await CreateDraftAsync(api, port);
                deviceId = draft.DeviceId;
                var version = await EditSampleAsync(api, deviceId, draft.InputId, draft.Version, 42);
                var initial = await BeginApplyAsync(api, version);
                await WaitForAsync(api, initial, status => status.GetProperty("stage").GetString() == "completed");
                using var peer = await AcceptAsync(receiver);
                var original = await ReadRequestAsync(peer.GetStream());
                Assert.Equal(42, Value(original)); // r1 队头不确认，应用/回滚不能撤回或改写它。
                version = await EditSampleAsync(api, deviceId, draft.InputId, version, 84);
                var next = await BeginApplyAsync(api, version);
                await WaitForAsync(api, next, status => status.GetProperty("stage").GetString() == "completed");
                await WaitForAsync(api, Api + "/runtime/status", _ => ReadDeliveries(data.Path).Any(row => row.Revision == 2));
                File.WriteAllText(Path.Combine(work.Path, ConfigurationTransactionGate.CommittedArmedFile), "armed");
                rollbackPath = await BeginRollbackInterruptionAsync(api, version);
                var checkpoint = await WaitForDurableCommitGateAsync(work.Path);
                Assert.Equal(Path.Combine(data.Path, ConfigDatabase.FileName), checkpoint.GetProperty("database").GetString());
                Assert.False(checkpoint.GetProperty("inTransaction").GetBoolean());
                Assert.True(checkpoint.GetProperty("originalConnectionClosed").GetBoolean());
                Assert.Equal(3, checkpoint.GetProperty("effective").GetInt64());
                Assert.Equal(2, checkpoint.GetProperty("previous").GetInt64());
                Assert.Equal(JsonValueKind.Null, checkpoint.GetProperty("pending").ValueKind);
                Assert.Equal(3, checkpoint.GetProperty("running").GetInt64());
                Assert.Equal(RuntimeSnapshotStates.History, checkpoint.GetProperty("oldState").GetString());
                Assert.Equal(RuntimeSnapshotStates.Effective, checkpoint.GetProperty("newState").GetString());
                Assert.Equal(AuditActions.SnapshotRolledBack, checkpoint.GetProperty("auditAction").GetString());
                Assert.Equal(1, checkpoint.GetProperty("commitAuditCount").GetInt64());
                Assert.Equal(0, checkpoint.GetProperty("appliedAuditCount").GetInt64());
                Assert.Equal("wal", checkpoint.GetProperty("journalMode").GetString());
                Assert.Equal(2, checkpoint.GetProperty("synchronous").GetInt64());
                Assert.Equal(5000, checkpoint.GetProperty("busyTimeout").GetInt64());
                var operation = await api.GetJsonAsync(rollbackPath, Token);
                Assert.Equal("rollback", operation.GetProperty("kind").GetString());
                Assert.Equal("committing", operation.GetProperty("stage").GetString());
                Assert.Equal(JsonValueKind.Null, operation.GetProperty("committed").ValueKind);
                Assert.Equal(3, operation.GetProperty("revision").GetInt64());
                var quiet = await api.GetJsonAsync(Api + "/runtime/status", Token);
                Assert.Equal(2, quiet.GetProperty("runtime").GetProperty("activeRevision").GetInt64());
                Assert.Equal("stopped", Input(quiet).GetProperty("state").GetString());
                Assert.Equal(0, quiet.GetProperty("inFlight").GetProperty("reservedBytes").GetInt64());
                AssertRollbackCommitState(state, abnormalExits: 0);
                var restored = await api.GetJsonAsync($"{Api}/devices/{deviceId}/inputs", Token);
                var restoredVersion = restored.GetProperty("version").GetString()!;
                Assert.NotEqual(version, restoredVersion);
                Assert.Equal("{\"value\":42}", restored.GetProperty("inputs")[0].GetProperty("simulator").GetProperty("sampleJson").GetString());
                var clean = await api.GetJsonAsync(Api + "/configuration/draft", Token);
                Assert.Equal(restoredVersion, clean.GetProperty("version").GetString());
                Assert.Equal(0, clean.GetProperty("changeCount").GetInt32());
                frozen = ReadDeliveries(data.Path);
                Assert.Equal(2, frozen.Length);
                Assert.Equal([1L, 2L], frozen.Select(row => row.Revision).ToArray());
                Assert.Equal(original.Body, Encoding.UTF8.GetBytes(frozen[0].Payload));
                Assert.Equal(frozen[0].SourceId, SourceMessageId(original));
                Assert.Contains($"Idempotency-Key: {frozen[0].Id}", original.Header, StringComparison.OrdinalIgnoreCase);
                Assert.All(frozen, row =>
                {
                    using var document = JsonDocument.Parse(row.Payload);
                    Assert.Equal(row.Revision == 1 ? 42 : 84, document.RootElement.GetProperty("values").GetProperty("value").GetInt64());
                });
                Assert.False(receiver.Pending());
                // 已持久替换草稿且写锁已释放，上层回滚仍未返回；此后编辑不能被旧调用覆盖。
                dirtyVersion = await EditSampleAsync(api, deviceId, draft.InputId, restoredVersion, 126);
                await AssertDurableCommitDirtyAsync(api, deviceId, dirtyVersion);
                Assert.Equal(1, (await api.GetJsonAsync(Api + "/configuration/draft", Token)).GetProperty("changeCount").GetInt32());
                Assert.False(File.Exists(Path.Combine(work.Path, ConfigurationTransactionGate.CommittedReleasedFile)));
                output.WriteLine("TEST DURABLE ROLLBACK CHECKPOINT: upper rollback committing/committed=null, runtime active r2/input stopped; durable effective/running r3, previous r2, pending empty; r3 rollback-to-r1 audit and draft42 replacement committed atomically; dirty126 saved afterwards; frozen r1/42+r2/84 unchanged, no r3 delivery.");
                if (!interrupt)
                {
                    File.Delete(Path.Combine(work.Path, ConfigurationTransactionGate.CommittedArmedFile));
                    File.WriteAllText(Path.Combine(work.Path, ConfigurationTransactionGate.CommittedReleasedFile), "normal rollback release");
                    var completed = await WaitForAsync(api, rollbackPath, status => status.GetProperty("stage").GetString() == "completed");
                    Assert.True(completed.GetProperty("committed").GetBoolean());
                    Assert.Equal(3, completed.GetProperty("revision").GetInt64());
                    await AckAsync(peer.GetStream());
                    await ReplayDurableCommitFrozenAsync(receiver, frozen.Skip(1));
                    await ReceiveRollbackFreshAsync(receiver, data.Path, frozen);
                    await WaitForAsync(api, Api + "/runtime/status", status =>
                        status.GetProperty("runtime").GetProperty("activeRevision").GetInt64() == 3 && ReadDeliveries(data.Path).Length == 0);
                    await AssertDurableCommitDirtyAsync(api, deviceId, dirtyVersion);
                    AssertRollbackCommitState(state, abnormalExits: 0);
                    await host.RequestStopAsync(Token);
                    Assert.Equal(HostExitCodes.Success, await host.WaitForExitAsync(Deadline, Token));
                    Assert.Null(state.Pointers().Running);
                    Assert.Contains("TEST DURABLE COMMIT GATE RELEASED", host.Output, StringComparison.Ordinal);
                    output.WriteLine("TEST DURABLE ROLLBACK RELEASED: real rollback returned, r3 active; original HTTP socket acknowledged, frozen r1/42+r2/84 replayed unchanged FIFO; fresh r3/42 not draft126, new source ID; dirty retained; no fourth version/automatic rollback; all abnormal counts0; queue empty, SIGTERM0, marker cleared.");
                    return;
                }
                using var queue = OpenQueue(data.Path);
                using var held = queue.BeginTransaction(deferred: false);
                Assert.Equal(frozen, ReadDeliveries(data.Path));
                await host.KillAsync(Token);
                killed = true;
                Assert.NotEqual(HostExitCodes.Success, await host.WaitForExitAsync(Deadline, Token));
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
        await using var restarted = Start(work.Path, data.Path, port);
        try
        {
            using var api = await ClientAsync(restarted, setup: false);
            AssertRollbackCommitState(state, abnormalExits: 1);
            await AssertDurableCommitDirtyAsync(api, deviceId, dirtyVersion);
            using var lost = await api.GetAsync(new Uri(rollbackPath, UriKind.Relative), Token);
            Assert.Equal(HttpStatusCode.NotFound, lost.StatusCode); // 进程内操作不伪造恢复，核对持久版本而非自动重试。
            await ReplayDurableCommitFrozenAsync(receiver, frozen);
            await ReceiveRollbackFreshAsync(receiver, data.Path, frozen);
            await WaitForAsync(api, Api + "/runtime/status", status =>
                status.GetProperty("runtime").GetProperty("activeRevision").GetInt64() == 3 && ReadDeliveries(data.Path).Length == 0);
            await AssertDurableCommitDirtyAsync(api, deviceId, dirtyVersion);
            AssertRollbackCommitState(state, abnormalExits: 1);
            await restarted.RequestStopAsync(Token);
            Assert.Equal(HostExitCodes.Success, await restarted.WaitForExitAsync(Deadline, Token));
            Assert.Null(state.Pointers().Running);
            Assert.Equal(1, state.Snapshot(3).AbnormalExitCount);
            output.WriteLine("TEST DURABLE ROLLBACK RECOVERED: unmodified product Host r3 rollback-to-r1; frozen r1/42+r2/84 unchanged FIFO and idempotency keys; fresh r3/42 not draft126, new source ID; dirty retained; old operation404, no automatic retry/new version/automatic rollback; abnormal r1=0/r2=0/r3=1; queue empty, SIGTERM0, marker cleared.");
        }
        finally { output.WriteLine(restarted.Output); }
    }

    private static async Task<string> BeginRollbackInterruptionAsync(HttpClient api, string version)
    {
        var review = await PostAsync(api, Api + "/configuration/versions/1/review", new { expectedVersion = version });
        Assert.True(review.GetProperty("preflightPassed").GetBoolean());
        using var accepted = await api.PostJsonAsync(Api + "/configuration/versions/1/rollback", new
        {
            expectedVersion = version,
            expectedEffectiveRevision = review.GetProperty("effectiveRevision"),
            expectedPlanHash = review.GetProperty("planHash").GetString(),
            confirmed = true,
        }, Token);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        return accepted.Headers.Location!.OriginalString;
    }

    private static void AssertRollbackCommitState(RuntimeStateReader state, int abnormalExits)
    {
        Assert.Equal((3L, (long?)2, (long?)null, (long?)3),
            (state.Pointers().Effective, state.Pointers().Previous, state.Pointers().Pending, state.Pointers().Running));
        Assert.Equal(3, state.Snapshots().Count);
        Assert.Equal(RuntimeSnapshotOrigins.Rollback, state.Snapshot(3).Origin);
        Assert.Equal("r1", state.Snapshot(3).OriginDetail);
        Assert.Equal(state.Snapshot(1).ContentHash, state.Snapshot(3).ContentHash);
        Assert.Equal(RuntimeSnapshotStates.History, state.Snapshot(1).State);
        Assert.Equal(RuntimeSnapshotStates.History, state.Snapshot(2).State);
        Assert.Equal(RuntimeSnapshotStates.Effective, state.Snapshot(3).State);
        Assert.Equal(0, state.Snapshot(1).AbnormalExitCount);
        Assert.Equal(0, state.Snapshot(2).AbnormalExitCount);
        Assert.Equal(abnormalExits, state.Snapshot(3).AbnormalExitCount);
        Assert.Equal(["r1", "r2"], state.Audit(AuditActions.SnapshotApplied).Select(entry => entry.Target).ToArray());
        var rollback = Assert.Single(state.Audit(AuditActions.SnapshotRolledBack));
        Assert.Equal("r3", rollback.Target);
        Assert.Equal("admin", rollback.Account);
        Assert.Contains("r1", rollback.Detail, StringComparison.Ordinal);
        Assert.Empty(state.Audit(AuditActions.AutoRollback));
        Assert.Empty(state.Events(RuntimeEventTypes.VersionNotEffective));
    }

    private static async Task ReceiveRollbackFreshAsync(TcpListener receiver, string data, DeliveryRow[] frozen)
    {
        using var peer = await AcceptAsync(receiver);
        var request = await ReadRequestAsync(peer.GetStream());
        Assert.Equal(42, Value(request));
        Assert.DoesNotContain(frozen, row => row.SourceId == SourceMessageId(request));
        Assert.Contains(ReadDeliveries(data), row => row.Revision == 3 && row.SourceId == SourceMessageId(request));
        await AckAsync(peer.GetStream());
    }
}
