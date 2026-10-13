using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
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
    public async Task RollbackDraftWrittenBeforeCommitRecoversOldVersionAndDraft(bool interrupt)
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "人工回滚草稿写事务内的真实 SIGKILL 恢复在 Linux 验证。");
        using var work = TemporaryDirectory.Create();
        using var data = TemporaryDirectory.Create();
        using var receiver = new TcpListener(IPAddress.Loopback, 0);
        receiver.Start();
        var port = ((IPEndPoint)receiver.LocalEndpoint).Port;
        output.WriteLine($"TEST ROLLBACK DRAFT WRITING HTTP PORT: {port}");
        var state = new RuntimeStateReader(data.Path);
        long deviceId;
        string rollbackPath;
        (string Version, string Content, string EditStamp) previousDraft;
        DeliveryRow[] frozen;
        await using (var host = HostProcess.StartTransactionWritingProbe(work.Path, data.Path, DeploymentArguments(port)))
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
                previousDraft = ReadRollbackWritingDraft(data.Path);
                Assert.Equal(version, previousDraft.Version);
                Assert.NotEqual(targetDraft.EditStamp, previousDraft.EditStamp);
                frozen = ReadDeliveries(data.Path);
                Assert.Equal([1L, 2L], frozen.Select(row => row.Revision).ToArray());
                Assert.Equal(original.Body, Encoding.UTF8.GetBytes(frozen[0].Payload));
                Assert.Equal(frozen[0].SourceId, SourceMessageId(original));
                Assert.Contains($"Idempotency-Key: {frozen[0].Id}", original.Header, StringComparison.OrdinalIgnoreCase);

                File.WriteAllText(Path.Combine(work.Path, ConfigurationTransactionGate.RollbackDraftArmedFile), "armed");
                rollbackPath = await BeginRollbackInterruptionAsync(api, version);
                var writing = await WaitForRollbackDraftWritingAsync(work.Path);
                Assert.Equal(Path.Combine(data.Path, ConfigDatabase.FileName), writing.GetProperty("database").GetString());
                Assert.True(writing.GetProperty("inTransaction").GetBoolean());
                Assert.Equal(3, writing.GetProperty("effective").GetInt64());
                Assert.Equal(2, writing.GetProperty("previous").GetInt64());
                Assert.Equal(JsonValueKind.Null, writing.GetProperty("pending").ValueKind);
                Assert.Equal(3, writing.GetProperty("running").GetInt64());
                Assert.Equal(RuntimeSnapshotStates.History, writing.GetProperty("oldState").GetString());
                Assert.Equal(RuntimeSnapshotStates.Effective, writing.GetProperty("newState").GetString());
                Assert.Equal(RuntimeSnapshotOrigins.Rollback, writing.GetProperty("origin").GetString());
                Assert.Equal("{\"value\":42}", writing.GetProperty("draftSampleJson").GetString());
                Assert.Equal(targetDraft.EditStamp, writing.GetProperty("draftEditStamp").GetString());
                var replacementVersion = writing.GetProperty("draftVersion").GetString()!;
                Assert.NotEqual(version, replacementVersion);
                Assert.Equal(1, writing.GetProperty("rollbackAuditCount").GetInt64());
                Assert.Equal("wal", writing.GetProperty("journalMode").GetString());
                Assert.Equal(2, writing.GetProperty("synchronous").GetInt64());
                Assert.Equal(5000, writing.GetProperty("busyTimeout").GetInt64());
                // 草稿查询 API 本身用 BEGIN IMMEDIATE 保持一致读取；此处用独立只读连接，不能在持锁窗口等待它。
                Assert.Equal(previousDraft, ReadRollbackWritingDraft(data.Path));
                AssertRollbackWritingOldState(state, restarted: false);
                var operation = await api.GetJsonAsync(rollbackPath, Token);
                Assert.Equal("rollback", operation.GetProperty("kind").GetString());
                Assert.Equal("committing", operation.GetProperty("stage").GetString());
                Assert.Equal(JsonValueKind.Null, operation.GetProperty("committed").ValueKind);
                Assert.Equal(3, operation.GetProperty("revision").GetInt64());
                var quiet = await api.GetJsonAsync(Api + "/runtime/status", Token);
                Assert.Equal(2, quiet.GetProperty("runtime").GetProperty("activeRevision").GetInt64());
                Assert.Equal("stopped", Input(quiet).GetProperty("state").GetString());
                Assert.Equal(0, quiet.GetProperty("inFlight").GetProperty("reservedBytes").GetInt64());
                Assert.Equal(frozen, ReadDeliveries(data.Path));
                Assert.False(receiver.Pending());
                Assert.False(File.Exists(Path.Combine(work.Path, ConfigurationTransactionGate.RollbackDraftReleasedFile)));
                output.WriteLine("TEST ROLLBACK DRAFT WRITING CHECKPOINT: writer transaction r3/previous2/pending empty/running3, draft42/new version/r1 edit stamp and rollback audit1; independent reader r2/previous1/pending3/running2, original draft84/version/r2 edit stamp and rollback audit0; runtime r2 stopped, no r3 delivery, frozen r1/42+r2/84 unchanged.");
                if (!interrupt)
                {
                    File.Delete(Path.Combine(work.Path, ConfigurationTransactionGate.RollbackDraftArmedFile));
                    File.WriteAllText(Path.Combine(work.Path, ConfigurationTransactionGate.RollbackDraftReleasedFile), "normal release");
                    var completed = await WaitForAsync(api, rollbackPath, status => status.GetProperty("stage").GetString() == "completed");
                    Assert.True(completed.GetProperty("committed").GetBoolean());
                    Assert.True(completed.GetProperty("result").GetProperty("draftReplaced").GetBoolean());
                    Assert.Equal(3, completed.GetProperty("revision").GetInt64());
                    AssertRollbackCommitState(state, abnormalExits: 0);
                    Assert.Equal((replacementVersion, targetDraft.Content, targetDraft.EditStamp), ReadRollbackWritingDraft(data.Path));
                    await AssertRollbackWritingDraftApiAsync(api, deviceId, replacementVersion, 42);
                    await AckAsync(peer.GetStream());
                    await ReplayDurableCommitFrozenAsync(receiver, frozen.Skip(1));
                    await ReceiveRollbackWritingFreshAsync(receiver, data.Path, frozen, revision: 3, value: 42);
                    await WaitForAsync(api, Api + "/runtime/status", _ => ReadDeliveries(data.Path).Length == 0);
                    await host.RequestStopAsync(Token);
                    Assert.Equal(HostExitCodes.Success, await host.WaitForExitAsync(Deadline, Token));
                    Assert.Null(state.Pointers().Running);
                    Assert.Contains("TEST ROLLBACK DRAFT WRITING GATE RELEASED", host.Output, StringComparison.Ordinal);
                    output.WriteLine("TEST ROLLBACK DRAFT WRITING RELEASED: original transaction committed, r3 active/draft42 replaced with r1 edit stamp/audit1; original HTTP socket acknowledged, frozen r1/42+r2/84 FIFO unchanged; fresh r3/42/new source; no r4/automatic rollback, all abnormal counts0; queue empty, SIGTERM0, marker cleared.");
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
                Assert.Equal(previousDraft, ReadRollbackWritingDraft(data.Path));
                AssertRollbackWritingOldState(state, restarted: false);
                Assert.DoesNotContain("TEST ROLLBACK DRAFT WRITING GATE RELEASED", host.Output, StringComparison.Ordinal);
            }
            finally
            {
                if (!killed && !File.Exists(Path.Combine(work.Path, ConfigurationTransactionGate.RollbackDraftReleasedFile)))
                { File.WriteAllText(Path.Combine(work.Path, ConfigurationTransactionGate.RollbackDraftReleasedFile), "test cleanup"); }
                output.WriteLine(host.Output);
            }
        }

        await using var restarted = Start(work.Path, data.Path, port);
        try
        {
            using var api = await ClientAsync(restarted, setup: false);
            AssertRollbackWritingOldState(state, restarted: true);
            Assert.Equal(previousDraft, ReadRollbackWritingDraft(data.Path));
            await AssertRollbackWritingDraftApiAsync(api, deviceId, previousDraft.Version, 84);
            using var lost = await api.GetAsync(new Uri(rollbackPath, UriKind.Relative), Token);
            await ProblemAssert.HasStatusAsync(lost, HttpStatusCode.NotFound, Token);
            Assert.Empty((await api.GetJsonAsync(Api + "/configuration/operations/", Token)).EnumerateArray());
            var history = await api.GetJsonAsync(Api + "/configuration/versions/", Token);
            var abandoned = Assert.Single(history.EnumerateArray(), row => row.GetProperty("revision").GetInt64() == 3);
            Assert.Equal(RuntimeSnapshotStates.NotEffective, abandoned.GetProperty("state").GetString());
            Assert.Equal(RuntimeVersionStore.InterruptedReason, abandoned.GetProperty("notEffectiveReason").GetString());
            await ReplayDurableCommitFrozenAsync(receiver, frozen);
            await ReceiveRollbackWritingFreshAsync(receiver, data.Path, frozen, revision: 2, value: 84);
            await WaitForAsync(api, Api + "/runtime/status", status =>
                status.GetProperty("runtime").GetProperty("activeRevision").GetInt64() == 2 && ReadDeliveries(data.Path).Length == 0);
            AssertRollbackWritingOldState(state, restarted: true);
            Assert.Equal(previousDraft, ReadRollbackWritingDraft(data.Path));
            await restarted.RequestStopAsync(Token);
            Assert.Equal(HostExitCodes.Success, await restarted.WaitForExitAsync(Deadline, Token));
            Assert.Null(state.Pointers().Running);
            output.WriteLine("TEST ROLLBACK DRAFT WRITING RECOVERED: unmodified Host r2, original draft84/version/r2 edit stamp preserved, rollback audit0; r3 notEffective/interrupted, floor3, old operation404, no automatic retry/r4/rollback; frozen r1/42+r2/84 FIFO/IDs/keys unchanged then fresh r2/84/new source; abnormal r1=0/r2=1/r3=0; queue empty, SIGTERM0, marker cleared.");
        }
        finally { output.WriteLine(restarted.Output); }
    }

    private static (string Version, string Content, string EditStamp) ReadRollbackWritingDraft(string data)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = Path.Combine(data, ConfigDatabase.FileName), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT version, (SELECT content FROM input_draft),
                (SELECT stamp FROM configuration_edit WHERE kind = 'input')
            FROM configuration_draft WHERE id = 1;
            """;
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        return (reader.GetString(0), reader.GetString(1), reader.GetString(2));
    }

    private static void AssertRollbackWritingOldState(RuntimeStateReader state, bool restarted)
    {
        var pointers = state.Pointers();
        Assert.Equal((2L, (long?)1, restarted ? null : (long?)3, (long?)2),
            (pointers.Effective, pointers.Previous, pointers.Pending, pointers.Running));
        Assert.Equal(3, pointers.RevisionFloor);
        Assert.Equal(3, state.Snapshots().Count);
        Assert.Equal(RuntimeSnapshotStates.History, state.Snapshot(1).State);
        Assert.Equal(RuntimeSnapshotStates.Effective, state.Snapshot(2).State);
        Assert.Equal(restarted ? RuntimeSnapshotStates.NotEffective : RuntimeSnapshotStates.Pending, state.Snapshot(3).State);
        Assert.Equal(restarted ? RuntimeVersionStore.InterruptedReason : null, state.Snapshot(3).NotEffectiveReason);
        Assert.Equal(RuntimeSnapshotOrigins.Rollback, state.Snapshot(3).Origin);
        Assert.Equal("r1", state.Snapshot(3).OriginDetail);
        Assert.Equal(state.Snapshot(1).ContentHash, state.Snapshot(3).ContentHash);
        Assert.Equal(0, state.Snapshot(1).AbnormalExitCount);
        Assert.Equal(restarted ? 1 : 0, state.Snapshot(2).AbnormalExitCount);
        Assert.Equal(0, state.Snapshot(3).AbnormalExitCount);
        Assert.Equal(["r1", "r2"], state.Audit(AuditActions.SnapshotApplied).Select(entry => entry.Target).ToArray());
        Assert.Empty(state.Audit(AuditActions.SnapshotRolledBack));
        Assert.Empty(state.Audit(AuditActions.AutoRollback));
        if (restarted)
        { Assert.Contains(RuntimeVersionStore.InterruptedReason, Assert.Single(state.Events(RuntimeEventTypes.VersionNotEffective), entry => entry.Target == "r3").Message, StringComparison.Ordinal); }
        else
        { Assert.Empty(state.Events(RuntimeEventTypes.VersionNotEffective)); }
    }

    private static async Task AssertRollbackWritingDraftApiAsync(HttpClient api, long deviceId, string version, int value)
    {
        var inputs = await api.GetJsonAsync($"{Api}/devices/{deviceId}/inputs", Token);
        Assert.Equal(version, inputs.GetProperty("version").GetString());
        Assert.Equal(JsonSerializer.Serialize(new { value }), Assert.Single(inputs.GetProperty("inputs").EnumerateArray())
            .GetProperty("simulator").GetProperty("sampleJson").GetString());
        var summary = await api.GetJsonAsync(Api + "/configuration/draft", Token);
        Assert.Equal(version, summary.GetProperty("version").GetString());
        Assert.Equal(0, summary.GetProperty("changeCount").GetInt32());
    }

    private static async Task ReceiveRollbackWritingFreshAsync(TcpListener receiver, string data, DeliveryRow[] frozen, long revision, int value)
    {
        using var peer = await AcceptAsync(receiver);
        var request = await ReadRequestAsync(peer.GetStream());
        Assert.Equal(value, Value(request));
        Assert.DoesNotContain(frozen, row => row.SourceId == SourceMessageId(request));
        Assert.Contains(ReadDeliveries(data), row => row.Revision == revision && row.SourceId == SourceMessageId(request));
        await AckAsync(peer.GetStream());
    }

    private static async Task<JsonElement> WaitForRollbackDraftWritingAsync(string directory)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
        deadline.CancelAfter(Deadline);
        var path = Path.Combine(directory, ConfigurationTransactionGate.RollbackDraftEnteredFile);
        while (!File.Exists(path))
        { await Task.Delay(10, deadline.Token); }
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path, deadline.Token));
        return document.RootElement.Clone();
    }
}
