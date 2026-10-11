using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using TenonEdge.Host.Bootstrap;
using TenonEdge.Host.Features.Audit;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Storage;
using TenonEdge.IntegrationTests.Infrastructure;

namespace TenonEdge.IntegrationTests.Processes;

/// <summary>总设计 §7.4 的真实 SIGKILL 子场景：受控准备中、旧消息排空中、排空后等待配置提交的写锁，以及新版本已提交且产生投递。
/// 提交前用测试持有的 SQLite 写锁确定边界，提交后核对真实新投递；不添加产品故障注入或重启接口，不是断电验证。</summary>
public sealed class ConfigurationInterruptionProcessTests(ITestOutputHelper output)
{
    private const string Api = "/api/edge-admin/v1";
    private static TimeSpan Deadline => TimeSpan.FromSeconds(30);
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [Trait("Category", "Process")]
    [InlineData("quiescing")]
    [InlineData("committing")]
    [InlineData("preparing")]
    public async Task KillBeforeConfigurationCommitDiscardsPendingWithoutApplyingDraftOrRewritingFrozenDelivery(string stage)
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "真实 SIGKILL 与数据目录锁验证只在 Linux 执行。");
        using var work = TemporaryDirectory.Create();
        using var data = TemporaryDirectory.Create();
        using var receiver = new TcpListener(IPAddress.Loopback, 0);
        receiver.Start();
        var port = ((IPEndPoint)receiver.LocalEndpoint).Port;
        var state = new RuntimeStateReader(data.Path);
        long deviceId;
        long inputId;
        string editedVersion;
        string interruptedPath;
        WireRequest frozen;
        DeliveryRow stored;
        DeliveryRow[] committed;

        await using (var host = stage == "preparing"
            ? HostProcess.StartPreparationProbe(work.Path, data.Path, DeploymentArguments(port)) : Start(work.Path, data.Path, port))
        {
            try
            {
                using var api = await ClientAsync(host, setup: true);
                var draft = await CreateDraftAsync(api, port);
                deviceId = draft.DeviceId;
                inputId = draft.InputId;
                var firstOperation = await BeginApplyAsync(api, draft.Version);
                await WaitForAsync(api, firstOperation, operation => operation.GetProperty("stage").GetString() == "completed");
                Assert.Equal((1L, (long?)null, (long?)null), (state.Pointers().Effective, state.Pointers().Previous, state.Pointers().Pending));

                // 真实 HTTP 发送已经开始，但不返回确认，保证已入库的第一条旧记录留在队列里。
                using var peer = await AcceptAsync(receiver);
                frozen = await ReadRequestAsync(peer.GetStream());
                Assert.Equal(42, Value(frozen));
                stored = ReadDeliveries(data.Path)[0];
                Assert.Equal(1, stored.Revision);
                Assert.Equal(stored.Payload, Encoding.UTF8.GetString(frozen.Body));
                Assert.Contains($"Idempotency-Key: {stored.Id}", frozen.Header, StringComparison.OrdinalIgnoreCase);

                editedVersion = await EditSampleAsync(api, deviceId, inputId, draft.Version, 84);

                using var connection = OpenQueue(data.Path);
                using var held = connection.BeginTransaction(deferred: false);
                // 新一次旧配置采样已读取但无法入库。WAL 读不被写锁阻断，管理 API 与静态预检仍可用。
                await WaitForAsync(api, Api + "/runtime/status", status =>
                    Input(status).GetProperty("reads").GetProperty("readsCompleted").GetInt64() >= 2 &&
                    status.GetProperty("inFlight").GetProperty("reservedBytes").GetInt64() > 0);
                if (stage == "preparing")
                { File.WriteAllText(Path.Combine(work.Path, PreparationProcessHost.ArmedFile), "armed"); }
                interruptedPath = await BeginApplyAsync(api, editedVersion);
                var pendingStage = stage == "preparing" ? "preparing" : "quiescing";
                await WaitForAsync(api, interruptedPath, operation => operation.GetProperty("stage").GetString() == pendingStage);
                var inputState = stage == "preparing" ? "running" : "stopped";
                var quiet = await WaitForAsync(api, Api + "/runtime/status", status => Input(status).GetProperty("state").GetString() == inputState);
                Assert.Equal(1, quiet.GetProperty("runtime").GetProperty("activeRevision").GetInt64());
                Assert.True(quiet.GetProperty("inFlight").GetProperty("reservedBytes").GetInt64() > 0);
                var operation = await api.GetJsonAsync(interruptedPath, Token);
                Assert.Equal(pendingStage, operation.GetProperty("stage").GetString());
                Assert.False(operation.GetProperty("committed").GetBoolean());
                Assert.Equal(2, operation.GetProperty("revision").GetInt64());
                Assert.Equal((1L, (long?)2, (long?)1), (state.Pointers().Effective, state.Pointers().Pending, state.Pointers().Running));
                Assert.Equal(RuntimeSnapshotStates.Pending, state.Snapshot(2).State);

                if (stage == "preparing")
                {
                    await WaitForPreparationGateAsync(work.Path);
                    Assert.False(File.Exists(Path.Combine(work.Path, PreparationProcessHost.ReleasedFile)));
                    var reads = Input(quiet).GetProperty("reads").GetProperty("readsCompleted").GetInt64();
                    held.Rollback(); // 准备阶段保持旧输入运行；不以队列写锁人为阻止它生成新投递。
                    var continuing = await WaitForAsync(api, Api + "/runtime/status", status =>
                        Input(status).GetProperty("reads").GetProperty("readsCompleted").GetInt64() > reads && ReadDeliveries(data.Path).Length > reads);
                    Assert.Equal("running", Input(continuing).GetProperty("state").GetString());
                    operation = await api.GetJsonAsync(interruptedPath, Token);
                    Assert.Equal("preparing", operation.GetProperty("stage").GetString());
                    Assert.False(operation.GetProperty("committed").GetBoolean());
                    Assert.Equal((1L, (long?)2, (long?)1), (state.Pointers().Effective, state.Pointers().Pending, state.Pointers().Running));
                }

                // 准备中的旧输入仍在运行。先证明它继续产生已提交记录，再固定强杀前的集合；
                // 不能假定读取队列到 SIGKILL 之间恰好不会发生下一次采样/提交。
                using var preparationQueueLock = stage == "preparing" ? connection.BeginTransaction(deferred: false) : null;

                // 先用队列写锁固定静默窗口，再在释放它之前取得配置写锁。这样冻结已经提交，
                // 旧分发可以真正排空，但第 4 步的 BEGIN IMMEDIATE 不能取得写锁或改变有效版本。
                // 这是提交事务开始前的窗口，不是事务内部或数据库已经提交、方法尚未返回的窗口。
                using var commitConnection = stage == "committing" ? OpenDatabase(data.Path, ConfigDatabase.FileName) : null;
                using var commitLock = commitConnection?.BeginTransaction(deferred: false);
                if (stage == "committing")
                {
                    held.Rollback();
                    operation = await WaitForAsync(api, interruptedPath, status => status.GetProperty("stage").GetString() == "committing");
                    // 提交中的公共进度有意为 null；是否提交由持久指针与所持写锁证明，不能误读为 false。
                    Assert.Equal(JsonValueKind.Null, operation.GetProperty("committed").ValueKind);
                    var drained = await api.GetJsonAsync(Api + "/runtime/status", Token);
                    Assert.Equal("stopped", Input(drained).GetProperty("state").GetString());
                    Assert.Equal("stopped", Assert.Single(drained.GetProperty("runtime").GetProperty("units").EnumerateArray(),
                        unit => unit.GetProperty("id").GetProperty("kind").GetString() == "delivery.dispatcher").GetProperty("state").GetString());
                    Assert.Equal(0, drained.GetProperty("inFlight").GetProperty("reservedBytes").GetInt64());
                    Assert.Equal(1, drained.GetProperty("runtime").GetProperty("activeRevision").GetInt64());
                    Assert.Equal((1L, (long?)2, (long?)1), (state.Pointers().Effective, state.Pointers().Pending, state.Pointers().Running));
                    Assert.Equal(RuntimeSnapshotStates.Effective, state.Snapshot(1).State);
                    Assert.Equal(RuntimeSnapshotStates.Pending, state.Snapshot(2).State);
                }
                committed = ReadDeliveries(data.Path);
                Assert.Equal(stored, committed[0]);
                Assert.All(committed, row =>
                {
                    Assert.Equal(1, row.Revision);
                    using var json = JsonDocument.Parse(row.Payload);
                    Assert.Equal(42, json.RootElement.GetProperty("values").GetProperty("value").GetInt64());
                });
                if (stage == "committing")
                { Assert.True(committed.Length >= 2, "静默期间的旧消息应已在配置提交前真正入库。"); }
                Assert.Single(state.Audit(AuditActions.SnapshotApplied));
                Assert.False(receiver.Pending()); // 新输入未激活，独立发送者仍只有先前的未确认请求。
                output.WriteLine($"强杀前：stage={operation.GetProperty("stage").GetString()}，输入={inputState}，" +
                    $"准备同步窗口={stage == "preparing"}，{state.Pointers()}，已入库 {committed.Length} 条，冻结队头 seq={stored.Seq} / {stored.Id}。");

                await host.KillAsync(Token); // 对应阶段的写锁仍持有，不能跑停机清理或赶在强杀前提交。
                if (stage == "committing")
                { commitLock!.Rollback(); }
                else if (stage == "preparing")
                { preparationQueueLock!.Rollback(); }
                else
                { held.Rollback(); }
                Assert.Equal(committed, ReadDeliveries(data.Path));
                Assert.Equal((1L, (long?)2, (long?)1), (state.Pointers().Effective, state.Pointers().Pending, state.Pointers().Running));
            }
            finally { output.WriteLine(host.Output); }
        }

        await using var restarted = Start(work.Path, data.Path, port);
        try
        {
            using var api = await ClientAsync(restarted, setup: false);
            Assert.Equal((1L, (long?)null, (long?)null, (long?)1),
                (state.Pointers().Effective, state.Pointers().Previous, state.Pointers().Pending, state.Pointers().Running));
            Assert.Equal((RuntimeSnapshotStates.NotEffective, RuntimeVersionStore.InterruptedReason, 0),
                (state.Snapshot(2).State, state.Snapshot(2).NotEffectiveReason, state.Snapshot(2).AbnormalExitCount));
            Assert.Equal(1, state.Snapshot(1).AbnormalExitCount);
            var interrupted = Assert.Single(state.Events(RuntimeEventTypes.VersionNotEffective), entry => entry.Target == "r2");
            Assert.Contains("进程中断", interrupted.Message, StringComparison.Ordinal);
            Assert.Single(state.Audit(AuditActions.SnapshotApplied));
            Assert.Equal(2, state.Snapshots().Count); // 启动恢复未重新应用草稿或生成版本。
            var history = await api.GetJsonAsync(Api + "/configuration/versions/", Token);
            var abandoned = Assert.Single(history.EnumerateArray(), row => row.GetProperty("revision").GetInt64() == 2);
            Assert.Equal(RuntimeSnapshotStates.NotEffective, abandoned.GetProperty("state").GetString());
            Assert.Equal(RuntimeVersionStore.InterruptedReason, abandoned.GetProperty("notEffectiveReason").GetString());
            output.WriteLine($"静默中断恢复：{state.Pointers()}，r2=未生效：进程中断，仍按 r1 采集。");

            var sources = await api.GetJsonAsync($"{Api}/devices/{deviceId}/inputs", Token);
            Assert.Equal(editedVersion, sources.GetProperty("version").GetString());
            Assert.Equal("{\"value\":84}", sources.GetProperty("inputs")[0].GetProperty("simulator").GetProperty("sampleJson").GetString());
            Assert.Empty((await api.GetJsonAsync(Api + "/configuration/operations/", Token)).EnumerateArray());
            using var missing = await api.GetAsync(new Uri(interruptedPath, UriKind.Relative), Token);
            await ProblemAssert.HasStatusAsync(missing, HttpStatusCode.NotFound, Token);

            // 重启先重发同一冻结 FIFO 队头：编号、正文和幂等请求头不按草稿重写。
            using (var replay = await AcceptAsync(receiver))
            {
                var request = await ReadRequestAsync(replay.GetStream());
                Assert.Equal(frozen.Body, request.Body);
                Assert.Contains($"Idempotency-Key: {stored.Id}", request.Header, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(stored, ReadDeliveries(data.Path)[0]);
                await AckAsync(replay.GetStream());
            }
            // 若锁取得前已产生其他旧记录，也按原样确认；不能把它们误当成重启后的新采样。
            foreach (var old in committed.Skip(1))
            {
                using var replay = await AcceptAsync(receiver);
                var request = await ReadRequestAsync(replay.GetStream());
                Assert.Equal(Encoding.UTF8.GetBytes(old.Payload), request.Body);
                Assert.Contains($"Idempotency-Key: {old.Id}", request.Header, StringComparison.OrdinalIgnoreCase);
                await AckAsync(replay.GetStream());
            }
            // 所有已入库旧记录之后是重启的新采样，不是未应用草稿 84，也不是任一旧源消息。
            using (var collected = await AcceptAsync(receiver))
            {
                var request = await ReadRequestAsync(collected.GetStream());
                Assert.Equal(42, Value(request));
                Assert.DoesNotContain(committed, row => row.SourceId == MessageId(request));
                await AckAsync(collected.GetStream());
            }
            var running = await api.GetJsonAsync(Api + "/runtime/status", Token);
            Assert.Equal("running", Input(running).GetProperty("state").GetString());
            await using (var contender = Start(work.Path, data.Path, port))
            {
                Assert.Equal(HostExitCodes.DataDirectoryInUse, await contender.WaitForExitAsync(Deadline, Token));
                output.WriteLine(contender.Output);
            }

            // 历史中未生效的 r2 不能回滚。明确重新预检/确认应用才生成 r3，r2 不复用。
            using var invalidRollback = await api.PostJsonAsync(Api + "/configuration/versions/2/review", new { expectedVersion = editedVersion }, Token);
            Assert.Equal(HttpStatusCode.Conflict, invalidRollback.StatusCode);
            var appliedPath = await BeginApplyAsync(api, editedVersion);
            var completed = await WaitForAsync(api, appliedPath, operation => operation.GetProperty("stage").GetString() == "completed");
            Assert.Equal(3, completed.GetProperty("revision").GetInt64());
            Assert.True(completed.GetProperty("committed").GetBoolean());
            Assert.Equal((3L, (long?)1, (long?)null), (state.Pointers().Effective, state.Pointers().Previous, state.Pointers().Pending));
            Assert.Equal(3, state.Snapshots().Count);
            Assert.Equal(["r1", "r3"], state.Audit(AuditActions.SnapshotApplied).Select(entry => entry.Target).ToArray());
            Assert.All(ReadDeliveries(data.Path), row => Assert.True(row.Revision is 1 or 3));
            using var receiveDeadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
            receiveDeadline.CancelAfter(Deadline);
            while (true)
            {
                using var next = await receiver.AcceptTcpClientAsync(receiveDeadline.Token);
                var request = await ReadRequestAsync(next.GetStream());
                var value = Value(request);
                Assert.True(value is 42 or 84);
                await AckAsync(next.GetStream());
                if (value == 84)
                { break; }
            }
            await restarted.RequestStopAsync(Token);
            Assert.Equal(HostExitCodes.Success, await restarted.WaitForExitAsync(Deadline, Token));
            Assert.Null(state.Pointers().Running);
            Assert.Equal(RuntimeSnapshotStates.NotEffective, state.Snapshot(2).State);
        }
        finally { output.WriteLine(restarted.Output); }
    }

    [Fact]
    [Trait("Category", "Process")]
    public async Task KillAfterNewVersionProducesDeliveryKeepsCommittedVersionAndIgnoresLaterDraft()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "真实 SIGKILL 恢复验证只在 Linux 执行。");
        using var work = TemporaryDirectory.Create();
        using var data = TemporaryDirectory.Create();
        using var receiver = new TcpListener(IPAddress.Loopback, 0);
        receiver.Start();
        var port = ((IPEndPoint)receiver.LocalEndpoint).Port;
        var state = new RuntimeStateReader(data.Path);
        long deviceId;
        string dirtyVersion;
        WireRequest frozen;
        DeliveryRow stored;

        await using (var host = Start(work.Path, data.Path, port))
        {
            try
            {
                using var api = await ClientAsync(host, setup: true);
                var draft = await CreateDraftAsync(api, port);
                deviceId = draft.DeviceId;
                var initial = await BeginApplyAsync(api, draft.Version);
                await WaitForAsync(api, initial, operation => operation.GetProperty("stage").GetString() == "completed");
                using (var peer = await AcceptAsync(receiver))
                {
                    var request = await ReadRequestAsync(peer.GetStream());
                    Assert.Equal(42, Value(request));
                    await AckAsync(peer.GetStream());
                }
                var nextVersion = await EditSampleAsync(api, deviceId, draft.InputId, draft.Version, 84);
                var applied = await BeginApplyAsync(api, nextVersion);
                // 可能仍有 r1 的已冻结记录。逐条确认，直到实际收到新输入生成的 84；不靠固定延迟猜测提交。
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
                deadline.CancelAfter(Deadline);
                while (true)
                {
                    using var peer = await receiver.AcceptTcpClientAsync(deadline.Token);
                    var request = await ReadRequestAsync(peer.GetStream());
                    var value = Value(request);
                    Assert.True(value is 42 or 84);
                    if (value == 42)
                    { await AckAsync(peer.GetStream()); continue; }
                    frozen = request;
                    stored = ReadDeliveries(data.Path)[0];
                    Assert.Equal(2, stored.Revision);
                    Assert.Equal(stored.Payload, Encoding.UTF8.GetString(frozen.Body));
                    Assert.Contains($"Idempotency-Key: {stored.Id}", frozen.Header, StringComparison.OrdinalIgnoreCase);
                    var completed = await WaitForAsync(api, applied, operation => operation.GetProperty("stage").GetString() == "completed");
                    Assert.True(completed.GetProperty("committed").GetBoolean());
                    // 再保存 126，但不应用。有效版本 r2 与其运行中标记已持久化，pending 为空。
                    dirtyVersion = await EditSampleAsync(api, deviceId, draft.InputId, nextVersion, 126);
                    Assert.Equal((2L, (long?)1, (long?)null, (long?)2),
                        (state.Pointers().Effective, state.Pointers().Previous, state.Pointers().Pending, state.Pointers().Running));
                    Assert.False(state.Snapshot(2).IsStable);
                    output.WriteLine($"强杀前：新版本已完成激活并发送 84，{state.Pointers()}，未确认队头 seq={stored.Seq} / {stored.Id}。");
                    await host.KillAsync(Token); // 新投递未获确认；不执行停机删除或回滚。
                    break;
                }
                Assert.Equal(stored, Assert.Single(ReadDeliveries(data.Path)));
            }
            finally { output.WriteLine(host.Output); }
        }

        await using var restarted = Start(work.Path, data.Path, port);
        try
        {
            using var api = await ClientAsync(restarted, setup: false);
            Assert.Equal((2L, (long?)1, (long?)null, (long?)2),
                (state.Pointers().Effective, state.Pointers().Previous, state.Pointers().Pending, state.Pointers().Running));
            Assert.Equal(2, state.Snapshots().Count);
            Assert.Equal(1, state.Snapshot(2).AbnormalExitCount);
            Assert.Equal(0, state.Snapshot(1).AbnormalExitCount);
            Assert.Equal(["r1", "r2"], state.Audit(AuditActions.SnapshotApplied).Select(entry => entry.Target).ToArray());
            Assert.Empty(state.Audit(AuditActions.AutoRollback));
            Assert.Empty(state.Events(RuntimeEventTypes.VersionNotEffective));
            output.WriteLine($"激活后中断恢复：{state.Pointers()}，r2 非正常退出次数=1，无自动回滚，草稿未自动应用。");
            var sources = await api.GetJsonAsync($"{Api}/devices/{deviceId}/inputs", Token);
            Assert.Equal(dirtyVersion, sources.GetProperty("version").GetString());
            Assert.Equal("{\"value\":126}", sources.GetProperty("inputs")[0].GetProperty("simulator").GetProperty("sampleJson").GetString());
            using (var replay = await AcceptAsync(receiver))
            {
                var request = await ReadRequestAsync(replay.GetStream());
                Assert.Equal(frozen.Body, request.Body);
                Assert.Contains($"Idempotency-Key: {stored.Id}", request.Header, StringComparison.OrdinalIgnoreCase);
                await AckAsync(replay.GetStream());
            }
            using (var collected = await AcceptAsync(receiver))
            {
                var request = await ReadRequestAsync(collected.GetStream());
                Assert.Equal(84, Value(request)); // 不加载 r1，也不采集未应用草稿 126。
                Assert.NotEqual(MessageId(frozen), MessageId(request));
                await AckAsync(collected.GetStream());
            }
            Assert.All(ReadDeliveries(data.Path), row => Assert.Equal(2, row.Revision));
            var running = await api.GetJsonAsync(Api + "/runtime/status", Token);
            Assert.Equal(2, running.GetProperty("runtime").GetProperty("activeRevision").GetInt64());
            Assert.Equal("running", Input(running).GetProperty("state").GetString());
            await restarted.RequestStopAsync(Token);
            Assert.Equal(HostExitCodes.Success, await restarted.WaitForExitAsync(Deadline, Token));
            Assert.Null(state.Pointers().Running);
        }
        finally { output.WriteLine(restarted.Output); }
    }

    [Fact]
    [Trait("Category", "Process")]
    public async Task PreparationProbeRunsRealSimulatorAfterReleaseAndStopsNormally()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "受控准备阶段与 SIGTERM 子进程只在 Linux 验证。");
        using var work = TemporaryDirectory.Create();
        using var data = TemporaryDirectory.Create();
        using var receiver = new TcpListener(IPAddress.Loopback, 0);
        receiver.Start();
        var port = ((IPEndPoint)receiver.LocalEndpoint).Port;
        var state = new RuntimeStateReader(data.Path);
        await using var host = HostProcess.StartPreparationProbe(work.Path, data.Path, DeploymentArguments(port));
        try
        {
            using var api = await ClientAsync(host, setup: true);
            var draft = await CreateDraftAsync(api, port);
            var initial = await BeginApplyAsync(api, draft.Version);
            await WaitForAsync(api, initial, operation => operation.GetProperty("stage").GetString() == "completed");
            using (var first = await AcceptAsync(receiver))
            {
                Assert.Equal(42, Value(await ReadRequestAsync(first.GetStream())));
                await AckAsync(first.GetStream());
            }
            var edited = await EditSampleAsync(api, draft.DeviceId, draft.InputId, draft.Version, 84);
            File.WriteAllText(Path.Combine(work.Path, PreparationProcessHost.ArmedFile), "armed");
            var applying = await BeginApplyAsync(api, edited);
            await WaitForPreparationGateAsync(work.Path);
            var preparing = await api.GetJsonAsync(applying, Token);
            Assert.Equal("preparing", preparing.GetProperty("stage").GetString());
            Assert.Equal((1L, (long?)2), (state.Pointers().Effective, state.Pointers().Pending));
            File.WriteAllText(Path.Combine(work.Path, PreparationProcessHost.ReleasedFile), "released");
            var completed = await WaitForAsync(api, applying, operation => operation.GetProperty("stage").GetString() == "completed");
            Assert.True(completed.GetProperty("committed").GetBoolean());
            Assert.Equal((2L, (long?)1, (long?)null), (state.Pointers().Effective, state.Pointers().Previous, state.Pointers().Pending));
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
            deadline.CancelAfter(Deadline);
            while (true)
            {
                using var next = await receiver.AcceptTcpClientAsync(deadline.Token);
                var request = await ReadRequestAsync(next.GetStream());
                var value = Value(request);
                Assert.True(value is 42 or 84);
                await AckAsync(next.GetStream());
                if (value == 84)
                { break; }
            }
            Assert.Contains("TEST PREPARATION GATE RELEASED", host.Output, StringComparison.Ordinal);
            await host.RequestStopAsync(Token);
            Assert.Equal(HostExitCodes.Success, await host.WaitForExitAsync(Deadline, Token));
            Assert.Null(state.Pointers().Running);
        }
        finally { output.WriteLine(host.Output); }
    }

    private static async Task WaitForPreparationGateAsync(string directory)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
        deadline.CancelAfter(Deadline);
        var path = Path.Combine(directory, PreparationProcessHost.EnteredFile);
        while (!File.Exists(path))
        { await Task.Delay(10, deadline.Token); }
        // 读到完整身份才算子进程已到达同步点，不能把 Create/Write 之间的短窗口当作已进入。
        while (await File.ReadAllTextAsync(path, deadline.Token) != "input.simulator/meter/sample")
        { await Task.Delay(10, deadline.Token); }
    }

    private static async Task<string> EditSampleAsync(HttpClient api, long deviceId, long inputId, string version, int value)
    {
        using var edit = await api.PutAsJsonAsync(new Uri($"{Api}/devices/{deviceId}/inputs/{inputId}", UriKind.Relative), new
        {
            expectedVersion = version,
            key = "sample",
            name = "Sample",
            simulator = new { periodMs = 60000, sampleJson = JsonSerializer.Serialize(new { value }) },
        }, Token);
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        return (await edit.ReadJsonAsync(Token)).GetProperty("version").GetString()!;
    }

    private static HostProcess Start(string work, string data, int port) => HostProcess.Start(work, Environments.Production, data, DeploymentArguments(port));

    private static string[] DeploymentArguments(int port) =>
        ["--TenonEdge:Outbound:LocalPorts:0", port.ToString(CultureInfo.InvariantCulture), "--TenonEdge:Runtime:StableAfterSeconds", "600"];

    private static async Task<HttpClient> ClientAsync(HostProcess host, bool setup)
    {
        var client = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() })
        { BaseAddress = await host.WaitForListeningAddressAsync(Deadline, Token), Timeout = Deadline };
        try
        {
            await host.WaitUntilReadyAsync(client, Deadline, Token);
            using var login = setup ? await client.CompleteSetupAsync(Token) : await client.LoginAsync("admin", ApiRequests.StrongPassword, Token);
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            await client.UseSessionAsync(login, Token);
            return client;
        }
        catch { client.Dispose(); throw; }
    }

    private static async Task<(long DeviceId, long InputId, string Version)> CreateDraftAsync(HttpClient api, int port)
    {
        var device = await PostAsync(api, Api + "/devices", new { expectedVersion = "0", key = "meter", name = "Meter", kind = "simulator" });
        var deviceId = device.GetProperty("device").GetProperty("id").GetInt64();
        var input = await PostAsync(api, $"{Api}/devices/{deviceId}/inputs", new
        {
            expectedVersion = device.GetProperty("version").GetString(),
            key = "sample",
            name = "Sample",
            simulator = new { periodMs = 2000, sampleJson = "{\"value\":42}" },
        });
        var inputId = input.GetProperty("input").GetProperty("id").GetInt64();
        var connection = await PostAsync(api, Api + "/connections", new
        {
            expectedVersion = input.GetProperty("version").GetString(),
            key = "http",
            name = "HTTP",
            kind = "http",
            http = new { baseAddress = $"http://127.0.0.1:{port}/api/", timeoutMs = 60000 },
        });
        var route = await PostAsync(api, Api + "/routes", new
        {
            expectedVersion = connection.GetProperty("version").GetString(),
            key = "route",
            name = "Route",
            enabled = true,
            graph = new
            {
                schemaVersion = 1,
                nodes = new object[]
                {
                    new { id = "in", type = "input.simulator", version = 1, name = "Input", config = new { sourceIds = new[] { inputId } } },
                    new { id = "out", type = "output.http", version = 1, name = "Output", config = new { connectionId = connection.GetProperty("connection").GetProperty("id").GetInt64() } },
                },
                edges = new[] { new { source = "in", target = "out" } },
                layout = new { nodes = new Dictionary<string, object>() },
            },
        });
        return (deviceId, inputId, route.GetProperty("version").GetString()!);
    }

    private static async Task<string> BeginApplyAsync(HttpClient api, string version)
    {
        var review = await PostAsync(api, Api + "/configuration/review", new { expectedVersion = version });
        Assert.True(review.GetProperty("preflightPassed").GetBoolean());
        using var accepted = await api.PostJsonAsync(Api + "/configuration/apply", new
        {
            expectedVersion = version,
            expectedEffectiveRevision = review.GetProperty("effectiveRevision"),
            expectedPlanHash = review.GetProperty("planHash").GetString(),
            confirmed = true,
        }, Token);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        return accepted.Headers.Location!.OriginalString;
    }

    private static async Task<JsonElement> PostAsync(HttpClient api, string path, object request)
    {
        using var response = await api.PostJsonAsync(path, request, Token);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Token));
        return await response.ReadJsonAsync(Token);
    }

    private static async Task<JsonElement> WaitForAsync(HttpClient api, string path, Func<JsonElement, bool> condition)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
        deadline.CancelAfter(Deadline);
        while (true)
        {
            var status = await api.GetJsonAsync(path, deadline.Token);
            if (condition(status))
            { return status; }
            await Task.Delay(25, deadline.Token);
        }
    }

    private static JsonElement Input(JsonElement status) => Assert.Single(status.GetProperty("runtime").GetProperty("units").EnumerateArray(),
        unit => unit.GetProperty("id").GetProperty("kind").GetString() == "input.simulator");

    private static SqliteConnection OpenQueue(string data) => OpenDatabase(data, "queue.db");

    private static SqliteConnection OpenDatabase(string data, string file)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = Path.Combine(data, file), Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    private static DeliveryRow[] ReadDeliveries(string data)
    {
        using var connection = OpenQueue(data);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT seq, delivery_id, revision, source_message_id, payload FROM delivery ORDER BY seq";
        using var reader = command.ExecuteReader();
        var rows = new List<DeliveryRow>();
        while (reader.Read())
        { rows.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2), reader.GetString(3), Encoding.UTF8.GetString((byte[])reader[4]))); }
        return rows.ToArray();
    }

    private static Task<TcpClient> AcceptAsync(TcpListener receiver) => receiver.AcceptTcpClientAsync(Token).AsTask().WaitAsync(Deadline, Token);

    private static async Task<WireRequest> ReadRequestAsync(NetworkStream stream)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
        deadline.CancelAfter(Deadline);
        var bytes = new List<byte>();
        var one = new byte[1];
        while (bytes.Count < 32768)
        {
            await stream.ReadExactlyAsync(one, deadline.Token);
            bytes.Add(one[0]);
            if (bytes.Count >= 4 && bytes[^4] == 13 && bytes[^3] == 10 && bytes[^2] == 13 && bytes[^1] == 10)
            { break; }
        }
        Assert.True(bytes.Count < 32768, "HTTP 测试接收端的头部超限。");
        var header = Encoding.ASCII.GetString(bytes.ToArray());
        Assert.StartsWith("POST /api/ HTTP/1.1", header, StringComparison.Ordinal);
        var length = int.Parse(header.Split("\r\n").Single(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)).Split(':')[1], CultureInfo.InvariantCulture);
        Assert.InRange(length, 1, 300000);
        var body = new byte[length];
        await stream.ReadExactlyAsync(body, deadline.Token);
        return new(header, body);
    }

    private static ValueTask AckAsync(NetworkStream stream) => stream.WriteAsync("HTTP/1.1 204 No Content\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(), Token);
    private static long Value(WireRequest request) { using var json = JsonDocument.Parse(request.Body); return json.RootElement.GetProperty("values").GetProperty("value").GetInt64(); }
    private static string MessageId(WireRequest request) { using var json = JsonDocument.Parse(request.Body); return json.RootElement.GetProperty("messageId").GetString()!; }
    private sealed record WireRequest(string Header, byte[] Body);
    private sealed record DeliveryRow(long Seq, string Id, long Revision, string SourceId, string Payload);
}
