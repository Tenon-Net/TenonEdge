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
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [Trait("Category", "Process")]
    public async Task CorruptSnapshotRecoveryHonorsDurableCommitBoundary(bool afterCommit, bool interrupt)
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "损坏快照恢复的真实 SIGKILL 窗口在 Linux 验证。");
        using var work = TemporaryDirectory.Create();
        using var data = TemporaryDirectory.Create();
        using var receiver = new TcpListener(IPAddress.Loopback, 0);
        receiver.Start();
        var port = ((IPEndPoint)receiver.LocalEndpoint).Port;
        var state = new RuntimeStateReader(data.Path);
        long deviceId;
        (string Version, string Content, string EditStamp) dirty;
        DeliveryRow[] frozen;
        // 用未注入的 Host 与真实 HTTP 发送建立历史/冻结队列；正常停机后才破坏有效快照。
        await using (var initial = Start(work.Path, data.Path, port))
        {
            try
            {
                using var api = await ClientAsync(initial, setup: true);
                var draft = await CreateDraftAsync(api, port);
                deviceId = draft.DeviceId;
                var version = await EditSampleAsync(api, deviceId, draft.InputId, draft.Version, 42);
                await WaitForAsync(api, await BeginApplyAsync(api, version), IsCompleted);
                using var peer = await AcceptAsync(receiver);
                var original = await ReadRequestAsync(peer.GetStream());
                Assert.Equal(42, Value(original));
                version = await EditSampleAsync(api, deviceId, draft.InputId, version, 84);
                await WaitForAsync(api, await BeginApplyAsync(api, version), IsCompleted);
                await WaitForAsync(api, Api + "/runtime/status", _ => ReadDeliveries(data.Path).Any(row => row.Revision == 2));
                version = await EditSampleAsync(api, deviceId, draft.InputId, version, 126);
                dirty = ReadRollbackWritingDraft(data.Path);
                Assert.Equal(version, dirty.Version);
                frozen = ReadDeliveries(data.Path);
                Assert.Equal([1L, 2L], frozen.Select(row => row.Revision).ToArray());
                Assert.Equal(original.Body, Encoding.UTF8.GetBytes(frozen[0].Payload));
                Assert.Equal(frozen[0].SourceId, SourceMessageId(original));
                Assert.Contains($"Idempotency-Key: {frozen[0].Id}", original.Header, StringComparison.OrdinalIgnoreCase);
                await initial.RequestStopAsync(Token);
                Assert.Equal(HostExitCodes.Success, await initial.WaitForExitAsync(Deadline, Token));
                Assert.Null(state.Pointers().Running);
                Assert.Equal(frozen, ReadDeliveries(data.Path));
            }
            finally { output.WriteLine(initial.Output); }
        }
        state.Execute("UPDATE runtime_snapshot SET content='{}' WHERE revision=2");
        var damaged = state.Snapshot(2);
        string operationPath;
        var armed = afterCommit ? ConfigurationTransactionGate.CommittedArmedFile : ConfigurationTransactionGate.ArmedFile;
        var released = afterCommit ? ConfigurationTransactionGate.CommittedReleasedFile : ConfigurationTransactionGate.ReleasedFile;
        var label = afterCommit ? "DURABLE COMMIT" : "TRANSACTION WRITING";
        await using (var controlled = afterCommit
            ? HostProcess.StartDurableCommitProbe(work.Path, data.Path, DeploymentArguments(port))
            : HostProcess.StartTransactionWritingProbe(work.Path, data.Path, DeploymentArguments(port)))
        {
            var killed = false;
            try
            {
                using var api = await FailedRecoveryClientAsync(controlled);
                Assert.Equal(damaged, state.Snapshot(2));
                Assert.Equal(dirty, ReadRollbackWritingDraft(data.Path));
                Assert.Equal(frozen, ReadDeliveries(data.Path));
                Assert.False(receiver.Pending());
                File.WriteAllText(Path.Combine(work.Path, armed), "armed");
                operationPath = await BeginCorruptRecoveryAsync(api, dirty.Version, frozen.Length);
                var checkpoint = afterCommit ? await WaitForDurableCommitGateAsync(work.Path) : await WaitForTransactionGateAsync(work.Path);
                Assert.Equal(Path.Combine(data.Path, ConfigDatabase.FileName), checkpoint.GetProperty("database").GetString());
                Assert.Equal(!afterCommit, checkpoint.GetProperty("inTransaction").GetBoolean());
                Assert.Equal(3, checkpoint.GetProperty("effective").GetInt64());
                Assert.Equal(2, checkpoint.GetProperty("previous").GetInt64());
                Assert.Equal(JsonValueKind.Null, checkpoint.GetProperty("pending").ValueKind);
                Assert.Equal(3, checkpoint.GetProperty("running").GetInt64());
                Assert.Equal(RuntimeSnapshotStates.History, checkpoint.GetProperty("oldState").GetString());
                Assert.Equal(RuntimeSnapshotStates.Effective, checkpoint.GetProperty("newState").GetString());
                Assert.Equal("wal", checkpoint.GetProperty("journalMode").GetString());
                Assert.Equal(2, checkpoint.GetProperty("synchronous").GetInt64());
                Assert.Equal(5000, checkpoint.GetProperty("busyTimeout").GetInt64());
                if (afterCommit)
                {
                    Assert.True(checkpoint.GetProperty("originalConnectionClosed").GetBoolean());
                    Assert.Equal(AuditActions.SnapshotRecovered, checkpoint.GetProperty("auditAction").GetString());
                    Assert.Equal(1, checkpoint.GetProperty("commitAuditCount").GetInt64());
                    Assert.Equal(0, checkpoint.GetProperty("appliedAuditCount").GetInt64());
                    AssertCorruptRecoveryCommitted(state, revision: 3, abnormalExits: 0);
                }
                else
                { AssertCorruptRecoveryUncommitted(state, restarted: false); }
                var operation = await api.GetJsonAsync(operationPath, Token);
                Assert.Equal("recovery", operation.GetProperty("kind").GetString());
                Assert.Equal("committing", operation.GetProperty("stage").GetString());
                Assert.Equal(JsonValueKind.Null, operation.GetProperty("committed").ValueKind);
                Assert.Equal(3, operation.GetProperty("revision").GetInt64());
                await AssertRecoveryNotActiveAsync(api);
                // 草稿 API 会取得配置写锁；此窗口用独立 WAL 读者核对真实持久草稿/编辑标记。
                Assert.Equal(dirty, ReadRollbackWritingDraft(data.Path));
                Assert.Equal(frozen, ReadDeliveries(data.Path));
                Assert.False(receiver.Pending());
                Assert.False(File.Exists(Path.Combine(work.Path, released)));
                output.WriteLine($"TEST RECOVERY {label} CHECKPOINT: writer r3/previous2/running3; independent effective={(afterCommit ? 3 : 2)}, audit={(afterCommit ? 1 : 0)}; no active runtime; dirty126/version/edit stamp and frozen r1/42+r2/84 unchanged.");
                if (!interrupt)
                {
                    File.Delete(Path.Combine(work.Path, armed));
                    File.WriteAllText(Path.Combine(work.Path, released), "normal release");
                    await AssertRecoveryCompletedAsync(api, operationPath, 3);
                    await FinishCorruptRecoveryAsync(controlled, api, receiver, state, data.Path, deviceId, dirty, frozen, 3, 0);
                    Assert.Contains($"TEST {label} GATE RELEASED", controlled.Output, StringComparison.Ordinal);
                    output.WriteLine($"TEST RECOVERY {label} RELEASED: original transaction completed, r3/42 active, dirty126 preserved; frozen FIFO/body/IDs/keys replayed, fresh r3/42, recovery audit1, no automatic replay; SIGTERM0 and running marker cleared.");
                    return;
                }
                await controlled.KillAsync(Token);
                killed = true;
                Assert.NotEqual(HostExitCodes.Success, await controlled.WaitForExitAsync(Deadline, Token));
                Assert.DoesNotContain($"TEST {label} GATE RELEASED", controlled.Output, StringComparison.Ordinal);
                Assert.Equal(frozen, ReadDeliveries(data.Path));
                Assert.Equal(dirty, ReadRollbackWritingDraft(data.Path));
                if (afterCommit)
                { AssertCorruptRecoveryCommitted(state, 3, 0); }
                else
                { AssertCorruptRecoveryUncommitted(state, restarted: false); }
            }
            finally
            {
                if (!killed && !File.Exists(Path.Combine(work.Path, released)))
                { File.WriteAllText(Path.Combine(work.Path, released), "test cleanup"); }
                output.WriteLine(controlled.Output);
            }
        }

        await using var restarted = Start(work.Path, data.Path, port); // 不使用 SQL 探针或测试工厂的产品 Host。
        try
        {
            using var api = afterCommit ? await ClientAsync(restarted, setup: false) : await FailedRecoveryClientAsync(restarted);
            using var missing = await api.GetAsync(new Uri(operationPath, UriKind.Relative), Token);
            await ProblemAssert.HasStatusAsync(missing, HttpStatusCode.NotFound, Token);
            Assert.Empty((await api.GetJsonAsync(Api + "/configuration/operations/", Token)).EnumerateArray());
            Assert.Equal(dirty, ReadRollbackWritingDraft(data.Path));
            long effective;
            if (afterCommit)
            {
                effective = 3;
                AssertCorruptRecoveryCommitted(state, 3, abnormalExits: 1);
            }
            else
            {
                AssertCorruptRecoveryUncommitted(state, restarted: true);
                Assert.Equal(damaged, state.Snapshot(2));
                Assert.Equal(frozen, ReadDeliveries(data.Path));
                Assert.False(receiver.Pending());
                // 不自动重放；管理员必须再次预检/确认，新版递增到 r4，不复用未生效 r3。
                var retry = await BeginCorruptRecoveryAsync(api, dirty.Version, frozen.Length);
                await AssertRecoveryCompletedAsync(api, retry, 4);
                effective = 4;
                output.WriteLine("TEST RECOVERY PRECOMMIT RESTART: still runFailed/ready503, corrupt r2 untouched; r3 notEffective/interrupted, no recovery audit or abnormal count, no output; explicit new review/confirmation creates r4, never automatic replay.");
            }
            await FinishCorruptRecoveryAsync(restarted, api, receiver, state, data.Path, deviceId, dirty, frozen, effective, afterCommit ? 1 : 0);
            output.WriteLine($"TEST RECOVERY {label} RESTARTED: unmodified Host, effective r{effective}/42, dirty126 unchanged, frozen FIFO/body/IDs/keys replayed then fresh42; recovery audit1; SIGTERM0 and running marker cleared.");
        }
        finally { output.WriteLine(restarted.Output); }
    }

    private static bool IsCompleted(JsonElement status) => status.GetProperty("stage").GetString() == "completed";

    private static async Task<HttpClient> FailedRecoveryClientAsync(HostProcess host)
    {
        var api = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() })
        { BaseAddress = await host.WaitForListeningAddressAsync(Deadline, Token), Timeout = Deadline };
        try
        {
            await host.WaitForApplicationStartedAsync(Deadline, Token);
            using var login = await api.LoginAsync("admin", ApiRequests.StrongPassword, Token);
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            await api.UseSessionAsync(login, Token);
            await AssertRecoveryNotActiveAsync(api);
            return api;
        }
        catch { api.Dispose(); throw; }
    }

    private static async Task AssertRecoveryNotActiveAsync(HttpClient api)
    {
        using var ready = await api.GetAsync(new Uri("/readyz", UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        var status = await api.GetJsonAsync(Api + "/runtime/status", Token);
        Assert.Equal("runFailed", status.GetProperty("coordinator").GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, status.GetProperty("runtime").GetProperty("activeRevision").ValueKind);
        var writer = Assert.Single(status.GetProperty("runtime").GetProperty("units").EnumerateArray());
        Assert.Equal("delivery.writer", writer.GetProperty("id").GetProperty("kind").GetString());
        Assert.Equal("running", writer.GetProperty("state").GetString());
        Assert.Equal(0, status.GetProperty("inFlight").GetProperty("reservedBytes").GetInt64());
    }

    private static async Task<string> BeginCorruptRecoveryAsync(HttpClient api, string version, int queuedCount)
    {
        var review = await PostAsync(api, Api + "/configuration/versions/1/recovery-review", new { expectedVersion = version });
        Assert.True(review.GetProperty("canConfirm").GetBoolean());
        Assert.Equal(queuedCount, review.GetProperty("queue").GetProperty("checkedCount").GetInt64());
        Assert.Equal(0, review.GetProperty("queue").GetProperty("blockedCount").GetInt64());
        using var accepted = await api.PostJsonAsync(Api + "/configuration/versions/1/recover", new
        {
            expectedVersion = version,
            confirmationHash = review.GetProperty("confirmationHash").GetString(),
            confirmed = true,
            preserveDraftConfirmed = true,
        }, Token);
        Assert.True(accepted.StatusCode == HttpStatusCode.Accepted, await accepted.Content.ReadAsStringAsync(Token));
        return accepted.Headers.Location!.OriginalString;
    }

    private static async Task AssertRecoveryCompletedAsync(HttpClient api, string path, long revision)
    {
        var completed = await WaitForAsync(api, path, IsCompleted);
        Assert.Equal("recovery", completed.GetProperty("kind").GetString());
        Assert.Equal(revision, completed.GetProperty("revision").GetInt64());
        Assert.True(completed.GetProperty("committed").GetBoolean());
        Assert.True(completed.GetProperty("result").GetProperty("activated").GetBoolean());
        Assert.False(completed.GetProperty("result").GetProperty("draftReplaced").GetBoolean());
    }

    private static void AssertCorruptRecoveryUncommitted(RuntimeStateReader state, bool restarted)
    {
        var pointers = state.Pointers();
        Assert.Equal((2L, (long?)1, restarted ? null : (long?)3, (long?)null),
            (pointers.Effective, pointers.Previous, pointers.Pending, pointers.Running));
        Assert.Equal(3, pointers.RevisionFloor);
        Assert.Equal(3, state.Snapshots().Count);
        Assert.Equal(RuntimeSnapshotStates.History, state.Snapshot(1).State);
        Assert.Equal(RuntimeSnapshotStates.Effective, state.Snapshot(2).State);
        var abandoned = state.Snapshot(3);
        Assert.Equal(restarted ? RuntimeSnapshotStates.NotEffective : RuntimeSnapshotStates.Pending, abandoned.State);
        Assert.Equal(restarted ? RuntimeVersionStore.InterruptedReason : null, abandoned.NotEffectiveReason);
        Assert.Equal(RuntimeSnapshotOrigins.Recovery, abandoned.Origin);
        Assert.Equal("r1; damaged r2", abandoned.OriginDetail);
        Assert.Equal(state.Snapshot(1).Content, abandoned.Content);
        Assert.Equal(state.Snapshot(1).ContentHash, abandoned.ContentHash);
        Assert.All(state.Snapshots(), row => Assert.Equal(0, row.AbnormalExitCount));
        Assert.Empty(state.Audit(AuditActions.SnapshotRecovered));
        Assert.Empty(state.Audit(AuditActions.AutoRollback));
        Assert.Empty(state.Audit(AuditActions.SnapshotRolledBack));
        Assert.Equal(["r1", "r2"], state.Audit(AuditActions.SnapshotApplied).Select(entry => entry.Target).ToArray());
        if (restarted)
        { Assert.Contains(RuntimeVersionStore.InterruptedReason, Assert.Single(state.Events(RuntimeEventTypes.VersionNotEffective)).Message, StringComparison.Ordinal); }
        else
        { Assert.Empty(state.Events(RuntimeEventTypes.VersionNotEffective)); }
    }

    private static void AssertCorruptRecoveryCommitted(RuntimeStateReader state, long revision, int abnormalExits)
    {
        var pointers = state.Pointers();
        Assert.Equal((revision, (long?)2, (long?)null, (long?)revision),
            (pointers.Effective, pointers.Previous, pointers.Pending, pointers.Running));
        Assert.Equal(revision, pointers.RevisionFloor);
        Assert.Equal(revision, state.Snapshots().Count);
        Assert.Equal(RuntimeSnapshotStates.History, state.Snapshot(1).State);
        Assert.Equal(RuntimeSnapshotStates.History, state.Snapshot(2).State);
        Assert.Equal("{}", state.Snapshot(2).Content);
        var restored = state.Snapshot(revision);
        Assert.Equal(RuntimeSnapshotStates.Effective, restored.State);
        Assert.Equal(RuntimeSnapshotOrigins.Recovery, restored.Origin);
        Assert.Equal("r1; damaged r2", restored.OriginDetail);
        Assert.Equal(state.Snapshot(1).Content, restored.Content);
        Assert.Equal(state.Snapshot(1).ContentHash, restored.ContentHash);
        Assert.Equal(abnormalExits, restored.AbnormalExitCount);
        Assert.All(state.Snapshots().Where(row => row.Revision != revision), row => Assert.Equal(0, row.AbnormalExitCount));
        var audit = Assert.Single(state.Audit(AuditActions.SnapshotRecovered));
        Assert.Equal($"r{revision}", audit.Target);
        Assert.Equal("admin", audit.Account);
        Assert.Equal(AuditResults.Success, audit.Result);
        Assert.Empty(state.Audit(AuditActions.AutoRollback));
        Assert.Empty(state.Audit(AuditActions.SnapshotRolledBack));
        Assert.Equal(["r1", "r2"], state.Audit(AuditActions.SnapshotApplied).Select(entry => entry.Target).ToArray());
    }

    private static async Task FinishCorruptRecoveryAsync(HostProcess host, HttpClient api, TcpListener receiver, RuntimeStateReader state,
        string data, long deviceId, (string Version, string Content, string EditStamp) dirty, DeliveryRow[] frozen, long revision, int abnormalExits)
    {
        AssertCorruptRecoveryCommitted(state, revision, abnormalExits);
        await ReplayDurableCommitFrozenAsync(receiver, frozen);
        await ReceiveRollbackWritingFreshAsync(receiver, data, frozen, revision, 42);
        await WaitForAsync(api, Api + "/runtime/status", status =>
            status.GetProperty("runtime").GetProperty("activeRevision").GetInt64() == revision && ReadDeliveries(data).Length == 0);
        await host.WaitUntilReadyAsync(api, Deadline, Token);
        await AssertDurableCommitDirtyAsync(api, deviceId, dirty.Version);
        Assert.Equal(dirty, ReadRollbackWritingDraft(data));
        AssertCorruptRecoveryCommitted(state, revision, abnormalExits);
        await host.RequestStopAsync(Token);
        Assert.Equal(HostExitCodes.Success, await host.WaitForExitAsync(Deadline, Token));
        Assert.Null(state.Pointers().Running);
        Assert.Equal(dirty, ReadRollbackWritingDraft(data));
    }
}
