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
    public async Task DurableRollbackKeepsDraftEditedAfterFreezeBeforeCommit(bool interrupt)
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "保留并发草稿的人工回滚 SIGKILL 恢复在 Linux 验证。");
        using var work = TemporaryDirectory.Create();
        using var data = TemporaryDirectory.Create();
        using var receiver = new TcpListener(IPAddress.Loopback, 0);
        receiver.Start();
        var port = ((IPEndPoint)receiver.LocalEndpoint).Port;
        output.WriteLine($"TEST ROLLBACK CONCURRENT DRAFT HTTP PORT: {port}");
        var state = new RuntimeStateReader(data.Path);
        long deviceId;
        string rollbackPath;
        (string Version, string Content, string EditStamp) dirty;
        DeliveryRow[] frozen;
        await using (var host = HostProcess.StartPreparationCommitProbe(work.Path, data.Path, DeploymentArguments(port)))
        {
            var killed = false;
            try
            {
                using var api = await ClientAsync(host, setup: true);
                var draft = await CreateDraftAsync(api, port);
                deviceId = draft.DeviceId;
                var version = await EditSampleAsync(api, deviceId, draft.InputId, draft.Version, 42);
                var first = await BeginApplyAsync(api, version);
                await WaitForAsync(api, first, status => status.GetProperty("stage").GetString() == "completed");
                var targetDraft = ReadRollbackWritingDraft(data.Path);
                using var peer = await AcceptAsync(receiver);
                var original = await ReadRequestAsync(peer.GetStream());
                Assert.Equal(42, Value(original));
                version = await EditSampleAsync(api, deviceId, draft.InputId, version, 84);
                var second = await BeginApplyAsync(api, version);
                await WaitForAsync(api, second, status => status.GetProperty("stage").GetString() == "completed");
                await WaitForAsync(api, Api + "/runtime/status", _ => ReadDeliveries(data.Path).Any(row => row.Revision == 2));
                var previousDraft = ReadRollbackWritingDraft(data.Path);
                Assert.Equal(version, previousDraft.Version);
                frozen = ReadDeliveries(data.Path);
                Assert.Equal([1L, 2L], frozen.Select(row => row.Revision).ToArray());
                Assert.Equal(original.Body, Encoding.UTF8.GetBytes(frozen[0].Payload));
                Assert.Equal(frozen[0].SourceId, SourceMessageId(original));
                Assert.Contains($"Idempotency-Key: {frozen[0].Id}", original.Header, StringComparison.OrdinalIgnoreCase);

                File.WriteAllText(Path.Combine(work.Path, ConfigurationInterruptionProcessHost.ArmedFile), "armed");
                File.WriteAllText(Path.Combine(work.Path, ConfigurationTransactionGate.CommittedArmedFile), "armed");
                rollbackPath = await BeginRollbackInterruptionAsync(api, version);
                await WaitForPreparationGateAsync(work.Path);
                AssertRollbackWritingOldState(state, restarted: false);
                Assert.Equal(previousDraft, ReadRollbackWritingDraft(data.Path));
                var preparing = await api.GetJsonAsync(rollbackPath, Token);
                Assert.Equal("rollback", preparing.GetProperty("kind").GetString());
                Assert.Equal("preparing", preparing.GetProperty("stage").GetString());
                Assert.Equal(3, preparing.GetProperty("revision").GetInt64());
                var active = await api.GetJsonAsync(Api + "/runtime/status", Token);
                Assert.Equal(2, active.GetProperty("runtime").GetProperty("activeRevision").GetInt64());
                Assert.Equal("running", Input(active).GetProperty("state").GetString());

                // 冻结事务已返回、提交尚未开始；另一个认证请求真实保存并发草稿，不是 COMMIT 后再编辑。
                var dirtyVersion = await EditSampleAsync(api, deviceId, draft.InputId, version, 126);
                dirty = ReadRollbackWritingDraft(data.Path);
                Assert.Equal(dirtyVersion, dirty.Version);
                Assert.NotEqual(previousDraft.Version, dirty.Version);
                Assert.NotEqual(previousDraft.Content, dirty.Content);
                Assert.NotEqual(previousDraft.EditStamp, dirty.EditStamp);
                Assert.NotEqual(targetDraft.EditStamp, dirty.EditStamp);
                await AssertRollbackConcurrentDraftAsync(api, deviceId, dirty.Version);
                AssertRollbackWritingOldState(state, restarted: false);
                Assert.Equal(frozen, ReadDeliveries(data.Path));
                Assert.False(File.Exists(Path.Combine(work.Path, ConfigurationTransactionGate.CommittedEnteredFile)));
                Assert.False(File.Exists(Path.Combine(work.Path, ConfigurationInterruptionProcessHost.ReleasedFile)));
                output.WriteLine("TEST ROLLBACK CONCURRENT DRAFT FROZEN: r3 pending/rollback-to-r1, effective/running r2; preparation held/input running; authenticated dirty126 saved with new version/edit stamp before commit, rollback audit0; frozen r1/42+r2/84 unchanged.");
                File.Delete(Path.Combine(work.Path, ConfigurationInterruptionProcessHost.ArmedFile));
                File.WriteAllText(Path.Combine(work.Path, ConfigurationInterruptionProcessHost.ReleasedFile), "continue with concurrent draft");

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
                AssertRollbackCommitState(state, abnormalExits: 0);
                Assert.Equal(3, state.Pointers().RevisionFloor);
                Assert.Equal(dirty, ReadRollbackWritingDraft(data.Path));
                await AssertRollbackConcurrentDraftAsync(api, deviceId, dirty.Version);
                var committing = await api.GetJsonAsync(rollbackPath, Token);
                Assert.Equal("committing", committing.GetProperty("stage").GetString());
                Assert.Equal(JsonValueKind.Null, committing.GetProperty("committed").ValueKind);
                Assert.Equal(3, committing.GetProperty("revision").GetInt64());
                var quiet = await api.GetJsonAsync(Api + "/runtime/status", Token);
                Assert.Equal(2, quiet.GetProperty("runtime").GetProperty("activeRevision").GetInt64());
                Assert.Equal("stopped", Input(quiet).GetProperty("state").GetString());
                Assert.Equal(0, quiet.GetProperty("inFlight").GetProperty("reservedBytes").GetInt64());
                Assert.Equal(frozen, ReadDeliveries(data.Path));
                Assert.False(receiver.Pending());
                Assert.False(File.Exists(Path.Combine(work.Path, ConfigurationTransactionGate.CommittedReleasedFile)));
                output.WriteLine("TEST ROLLBACK CONCURRENT DRAFT COMMITTED: durable r3/previous2/pending empty/running3, rollback audit1; upper committing/committed=null, runtime r2 stopped; precommit dirty126/version/full input JSON/edit stamp unchanged, changeCount1; no r3 delivery, frozen FIFO unchanged.");
                if (!interrupt)
                {
                    File.Delete(Path.Combine(work.Path, ConfigurationTransactionGate.CommittedArmedFile));
                    File.WriteAllText(Path.Combine(work.Path, ConfigurationTransactionGate.CommittedReleasedFile), "normal release");
                    var completed = await WaitForAsync(api, rollbackPath, status => status.GetProperty("stage").GetString() == "completed");
                    Assert.True(completed.GetProperty("committed").GetBoolean());
                    Assert.Equal(3, completed.GetProperty("revision").GetInt64());
                    var result = completed.GetProperty("result");
                    Assert.True(result.GetProperty("activated").GetBoolean());
                    Assert.False(result.GetProperty("draftReplaced").GetBoolean());
                    Assert.Equal("runtime.rolledBack", result.GetProperty("code").GetString());
                    await AckAsync(peer.GetStream());
                    await ReplayDurableCommitFrozenAsync(receiver, frozen.Skip(1));
                    await ReceiveRollbackFreshAsync(receiver, data.Path, frozen);
                    await WaitForAsync(api, Api + "/runtime/status", status =>
                        status.GetProperty("runtime").GetProperty("activeRevision").GetInt64() == 3 && ReadDeliveries(data.Path).Length == 0);
                    AssertRollbackCommitState(state, abnormalExits: 0);
                    Assert.Equal(dirty, ReadRollbackWritingDraft(data.Path));
                    await AssertRollbackConcurrentDraftAsync(api, deviceId, dirty.Version);
                    await host.RequestStopAsync(Token);
                    Assert.Equal(HostExitCodes.Success, await host.WaitForExitAsync(Deadline, Token));
                    Assert.Null(state.Pointers().Running);
                    Assert.Contains("TEST DURABLE COMMIT GATE RELEASED", host.Output, StringComparison.Ordinal);
                    output.WriteLine("TEST ROLLBACK CONCURRENT DRAFT RELEASED: completed/activated/draftReplaced=false, r3/42 not dirty126; original HTTP socket acknowledged, frozen FIFO/payload/source IDs/keys unchanged; precommit dirty/version/full input JSON/edit stamp retained; no r4/automatic rollback, all abnormal counts0; queue empty, SIGTERM0, marker cleared.");
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
                Assert.Equal(dirty, ReadRollbackWritingDraft(data.Path));
                AssertRollbackCommitState(state, abnormalExits: 0);
                Assert.DoesNotContain("TEST DURABLE COMMIT GATE RELEASED", host.Output, StringComparison.Ordinal);
            }
            finally
            {
                if (!killed)
                {
                    File.WriteAllText(Path.Combine(work.Path, ConfigurationInterruptionProcessHost.ReleasedFile), "test cleanup");
                    File.WriteAllText(Path.Combine(work.Path, ConfigurationTransactionGate.CommittedReleasedFile), "test cleanup");
                }
                output.WriteLine(host.Output);
            }
        }

        await using var restarted = Start(work.Path, data.Path, port);
        try
        {
            using var api = await ClientAsync(restarted, setup: false);
            AssertRollbackCommitState(state, abnormalExits: 1);
            Assert.Equal(3, state.Pointers().RevisionFloor);
            Assert.Equal(dirty, ReadRollbackWritingDraft(data.Path));
            await AssertRollbackConcurrentDraftAsync(api, deviceId, dirty.Version);
            using var lost = await api.GetAsync(new Uri(rollbackPath, UriKind.Relative), Token);
            await ProblemAssert.HasStatusAsync(lost, HttpStatusCode.NotFound, Token);
            Assert.Empty((await api.GetJsonAsync(Api + "/configuration/operations/", Token)).EnumerateArray());
            await ReplayDurableCommitFrozenAsync(receiver, frozen);
            await ReceiveRollbackFreshAsync(receiver, data.Path, frozen);
            await WaitForAsync(api, Api + "/runtime/status", status =>
                status.GetProperty("runtime").GetProperty("activeRevision").GetInt64() == 3 && ReadDeliveries(data.Path).Length == 0);
            AssertRollbackCommitState(state, abnormalExits: 1);
            Assert.Equal(dirty, ReadRollbackWritingDraft(data.Path));
            await AssertRollbackConcurrentDraftAsync(api, deviceId, dirty.Version);
            await restarted.RequestStopAsync(Token);
            Assert.Equal(HostExitCodes.Success, await restarted.WaitForExitAsync(Deadline, Token));
            Assert.Null(state.Pointers().Running);
            output.WriteLine("TEST ROLLBACK CONCURRENT DRAFT RECOVERED: unmodified Host r3/42 not dirty126; precommit dirty/version/full input JSON/edit stamp retained, changeCount1; frozen r1/42+r2/84 FIFO/payload/source IDs/keys unchanged, fresh r3/new source; old operation404, no retry/r4/automatic rollback, abnormal r1=0/r2=0/r3=1; queue empty, SIGTERM0, marker cleared.");
        }
        finally { output.WriteLine(restarted.Output); }
    }

    private static async Task AssertRollbackConcurrentDraftAsync(HttpClient api, long deviceId, string version)
    {
        await AssertDurableCommitDirtyAsync(api, deviceId, version);
        var summary = await api.GetJsonAsync(Api + "/configuration/draft", Token);
        Assert.Equal(version, summary.GetProperty("version").GetString());
        Assert.Equal(1, summary.GetProperty("changeCount").GetInt32());
    }
}
