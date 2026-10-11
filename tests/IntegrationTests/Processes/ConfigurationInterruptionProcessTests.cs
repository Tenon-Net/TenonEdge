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

/// <summary>总设计 §7.4 的真实 SIGKILL 子场景：准备中、部分输入已停、旧消息排空中、等待提交写锁、事务写入后、提交后输入未启动及新版本产生投递。
/// 准备/部分停止/提交后输入未启动使用测试侧工厂，事务内部用原 SQL 回调，其余使用原产品 Host；无产品故障开关或重启接口，不是断电验证。</summary>
public sealed class ConfigurationInterruptionProcessTests(ITestOutputHelper output)
{
    private const string Api = "/api/edge-admin/v1";
    private static TimeSpan Deadline => TimeSpan.FromSeconds(30);
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void FreshSampleIdentityIsSourceMessageIdEvenWhenDeliveryIdentityDiffers()
    {
        const string source = "00000000-0000-4000-8000-000000000001";
        const string delivery = "00000000-0000-4000-8000-000000000002";
        var request = new WireRequest(string.Empty, JsonSerializer.SerializeToUtf8Bytes(new
        { messageId = delivery, sourceMessageId = source }));
        Assert.Equal(source, SourceMessageId(request));
        var anotherDelivery = new WireRequest(string.Empty, JsonSerializer.SerializeToUtf8Bytes(new
        { messageId = "00000000-0000-4000-8000-000000000003", sourceMessageId = source }));
        Assert.Equal(SourceMessageId(request), SourceMessageId(anotherDelivery));
    }

    [Theory]
    [Trait("Category", "Process")]
    [InlineData("quiescing")]
    [InlineData("committing")]
    [InlineData("preparing")]
    [InlineData("partial-stop")]
    [InlineData("transaction-writing")]
    public async Task KillBeforeConfigurationCommitDiscardsPendingWithoutApplyingDraftOrRewritingFrozenDelivery(string stage)
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "真实 SIGKILL 与数据目录锁验证只在 Linux 执行。");
        using var work = TemporaryDirectory.Create();
        using var data = TemporaryDirectory.Create();
        using var receiver = new TcpListener(IPAddress.Loopback, 0);
        receiver.Start();
        var port = ((IPEndPoint)receiver.LocalEndpoint).Port;
        var partialStop = stage == "partial-stop";
        var transactionWriting = stage == "transaction-writing";
        var draftValue = transactionWriting ? 126 : 84;
        long[] oldValues = partialStop ? [42, 43] : [42];
        var state = new RuntimeStateReader(data.Path);
        long deviceId;
        long inputId;
        long? slowDeviceId;
        string editedVersion;
        string interruptedPath;
        WireRequest frozen;
        DeliveryRow stored;
        DeliveryRow[] committed;

        await using (var host = stage switch
        {
            "preparing" => HostProcess.StartPreparationProbe(work.Path, data.Path, DeploymentArguments(port)),
            "partial-stop" => HostProcess.StartPartialStopProbe(work.Path, data.Path, DeploymentArguments(port)),
            "transaction-writing" => HostProcess.StartTransactionWritingProbe(work.Path, data.Path, DeploymentArguments(port)),
            _ => Start(work.Path, data.Path, port),
        })
        {
            try
            {
                using var api = await ClientAsync(host, setup: true);
                var draft = await CreateDraftAsync(api, port, partialStop);
                deviceId = draft.DeviceId;
                inputId = draft.InputId;
                slowDeviceId = draft.SlowDeviceId;
                var firstOperation = await BeginApplyAsync(api, draft.Version);
                await WaitForAsync(api, firstOperation, operation => operation.GetProperty("stage").GetString() == "completed");
                Assert.Equal((1L, (long?)null, (long?)null), (state.Pointers().Effective, state.Pointers().Previous, state.Pointers().Pending));

                // 真实 HTTP 发送已经开始，但不返回确认，保证已入库的第一条旧记录留在队列里。
                using var peer = await AcceptAsync(receiver);
                frozen = await ReadRequestAsync(peer.GetStream());
                Assert.Contains(Value(frozen), oldValues);
                stored = ReadDeliveries(data.Path)[0];
                Assert.Equal(1, stored.Revision);
                Assert.Equal(stored.SourceId, SourceMessageId(frozen));
                Assert.Equal(stored.Payload, Encoding.UTF8.GetString(frozen.Body));
                Assert.Contains($"Idempotency-Key: {stored.Id}", frozen.Header, StringComparison.OrdinalIgnoreCase);
                if (partialStop)
                {
                    await WaitForAsync(api, Api + "/runtime/status", _ => ReadDeliveries(data.Path).Select(row =>
                    {
                        using var json = JsonDocument.Parse(row.Payload);
                        return json.RootElement.GetProperty("values").GetProperty("value").GetInt64();
                    }).Distinct().Count() == 2);
                }

                editedVersion = await EditSampleAsync(api, deviceId, inputId, draft.Version, 84);

                using var connection = OpenQueue(data.Path);
                using var held = connection.BeginTransaction(deferred: false);
                // 新一次旧配置采样已读取但无法入库。WAL 读不被写锁阻断，管理 API 与静态预检仍可用。
                await WaitForAsync(api, Api + "/runtime/status", status =>
                    Input(status).GetProperty("reads").GetProperty("readsCompleted").GetInt64() >= 2 &&
                    (!partialStop || Input(status, ConfigurationInterruptionProcessHost.SlowUnitKey).GetProperty("reads").GetProperty("readsCompleted").GetInt64() >= 2) &&
                    status.GetProperty("inFlight").GetProperty("reservedBytes").GetInt64() > 0);
                if (stage == "preparing")
                { File.WriteAllText(Path.Combine(work.Path, ConfigurationInterruptionProcessHost.ArmedFile), "armed"); }
                else if (partialStop)
                { File.WriteAllText(Path.Combine(work.Path, ConfigurationInterruptionProcessHost.StopArmedFile), "armed"); }
                else if (transactionWriting)
                { File.WriteAllText(Path.Combine(work.Path, ConfigurationTransactionGate.ArmedFile), "armed"); }
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
                var frozenSnapshot = transactionWriting ? state.Snapshot(2) : null;

                if (transactionWriting)
                {
                    // 本次已冻结 84；在队列仍阻止静默完成时保存 126，避免在写事务中等待另一个写请求。
                    editedVersion = await EditSampleAsync(api, deviceId, inputId, editedVersion, draftValue);
                    held.Rollback();
                    var writing = await WaitForTransactionGateAsync(work.Path);
                    AssertTransactionWriting(writing, data.Path);
                    Assert.False(File.Exists(Path.Combine(work.Path, ConfigurationTransactionGate.ReleasedFile)));
                    operation = await api.GetJsonAsync(interruptedPath, Token);
                    Assert.Equal("committing", operation.GetProperty("stage").GetString());
                    Assert.Equal(JsonValueKind.Null, operation.GetProperty("committed").ValueKind);
                    var drained = await api.GetJsonAsync(Api + "/runtime/status", Token);
                    Assert.Equal("stopped", Input(drained).GetProperty("state").GetString());
                    Assert.Equal("stopped", Assert.Single(drained.GetProperty("runtime").GetProperty("units").EnumerateArray(),
                        unit => unit.GetProperty("id").GetProperty("kind").GetString() == "delivery.dispatcher").GetProperty("state").GetString());
                    Assert.Equal(0, drained.GetProperty("inFlight").GetProperty("reservedBytes").GetInt64());
                    Assert.Equal(1, drained.GetProperty("runtime").GetProperty("activeRevision").GetInt64());
                    Assert.Equal((1L, (long?)null, (long?)2, (long?)1),
                        (state.Pointers().Effective, state.Pointers().Previous, state.Pointers().Pending, state.Pointers().Running));
                    Assert.Equal(RuntimeSnapshotStates.Effective, state.Snapshot(1).State);
                    Assert.Equal(frozenSnapshot, state.Snapshot(2));
                    Assert.Equal(2, state.Pointers().RevisionFloor);
                    output.WriteLine("事务内部：写连接已有 effective/running=r2、previous=r1、pending 空、r1 history/r2 effective；事务仍开启，外部仍为 r1/pending r2，后续草稿 126 已保存。");
                }

                if (partialStop)
                {
                    await WaitForGateAsync(work.Path, ConfigurationInterruptionProcessHost.StopEnteredFile,
                        ConfigurationInterruptionProcessHost.SlowIdentity);
                    quiet = await api.GetJsonAsync(Api + "/runtime/status", Token);
                    AssertPartialStop(quiet);
                    Assert.False(File.Exists(Path.Combine(work.Path, ConfigurationInterruptionProcessHost.StopReleasedFile)));
                    Assert.False(File.Exists(Path.Combine(work.Path, ConfigurationInterruptionProcessHost.StopDisposedFile)));
                    var dispatchers = quiet.GetProperty("runtime").GetProperty("units").EnumerateArray()
                        .Where(unit => unit.GetProperty("id").GetProperty("kind").GetString() == "delivery.dispatcher").ToArray();
                    Assert.Equal(2, dispatchers.Length);
                    Assert.All(dispatchers, unit => Assert.Equal("running", unit.GetProperty("state").GetString()));
                    output.WriteLine("部分停止：sample=stopped，slow-stop=stopping；两个旧分发器仍 running，停止同步点尚未释放/销毁。");
                }

                if (stage == "preparing")
                {
                    await WaitForPreparationGateAsync(work.Path);
                    Assert.False(File.Exists(Path.Combine(work.Path, ConfigurationInterruptionProcessHost.ReleasedFile)));
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
                    var value = json.RootElement.GetProperty("values").GetProperty("value").GetInt64();
                    Assert.Contains(value, oldValues);
                    if (partialStop)
                    { Assert.Equal(value == 42 ? "meter" : ConfigurationInterruptionProcessHost.SlowDeviceKey, json.RootElement.GetProperty("deviceKey").GetString()); }
                    Assert.Equal(row.SourceId, json.RootElement.GetProperty("sourceMessageId").GetString());
                });
                if (stage == "committing" || transactionWriting)
                { Assert.True(committed.Length >= 2, "静默期间的旧消息应已在配置提交前真正入库。"); }
                if (partialStop)
                {
                    Assert.Equal(oldValues, committed.Select(row =>
                    {
                        using var json = JsonDocument.Parse(row.Payload);
                        return json.RootElement.GetProperty("values").GetProperty("value").GetInt64();
                    }).Distinct().Order().ToArray());
                }
                Assert.Single(state.Audit(AuditActions.SnapshotApplied));
                Assert.False(receiver.Pending()); // 新输入未激活，独立发送者仍只有先前的未确认请求。
                output.WriteLine($"强杀前：stage={operation.GetProperty("stage").GetString()}，输入={(partialStop ? "sample stopped / slow-stop stopping" : inputState)}，" +
                    $"准备同步窗口={stage == "preparing"}，{state.Pointers()}，已入库 {committed.Length} 条，冻结队头 seq={stored.Seq} / {stored.Id}。");

                await host.KillAsync(Token); // 对应阶段的写锁或同步点未释放，不能跑停机清理或赶在强杀前提交。
                if (stage == "committing")
                { commitLock!.Rollback(); }
                else if (stage == "preparing")
                { preparationQueueLock!.Rollback(); }
                else if (!transactionWriting)
                { held.Rollback(); }
                Assert.Equal(committed, ReadDeliveries(data.Path));
                Assert.Equal((1L, (long?)2, (long?)1), (state.Pointers().Effective, state.Pointers().Pending, state.Pointers().Running));
                if (transactionWriting)
                {
                    Assert.Equal(frozenSnapshot, state.Snapshot(2));
                    Assert.Equal(RuntimeSnapshotStates.Effective, state.Snapshot(1).State);
                    Assert.Single(state.Audit(AuditActions.SnapshotApplied));
                    Assert.False(File.Exists(Path.Combine(work.Path, ConfigurationTransactionGate.ReleasedFile)));
                }
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
            Assert.Equal(JsonSerializer.Serialize(new { value = draftValue }), Assert.Single(sources.GetProperty("inputs").EnumerateArray(),
                input => input.GetProperty("key").GetString() == "sample").GetProperty("simulator").GetProperty("sampleJson").GetString());
            if (partialStop)
            {
                var slowSources = await api.GetJsonAsync($"{Api}/devices/{slowDeviceId}/inputs", Token);
                Assert.Equal(editedVersion, slowSources.GetProperty("version").GetString());
                Assert.Equal("{\"value\":43}", Assert.Single(slowSources.GetProperty("inputs").EnumerateArray(),
                    input => input.GetProperty("key").GetString() == ConfigurationInterruptionProcessHost.SlowInputKey)
                    .GetProperty("simulator").GetProperty("sampleJson").GetString());
            }
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
            // 先排完所有已提交旧记录，再核对新源消息。双输入各有不同的真实值，必须两路都恢复，
            // 而且对照 sourceMessageId；不能拿 deliveryId 与源编号相比而得到恒真的“不在旧集合”。
            var recoveredValues = new HashSet<long>();
            using var samplingDeadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
            samplingDeadline.CancelAfter(Deadline);
            while (recoveredValues.Count < oldValues.Length)
            {
                using var collected = await receiver.AcceptTcpClientAsync(samplingDeadline.Token);
                var request = await ReadRequestAsync(collected.GetStream());
                Assert.Contains(Value(request), oldValues);
                Assert.DoesNotContain(committed, row => row.SourceId == SourceMessageId(request));
                if (partialStop)
                {
                    using var json = JsonDocument.Parse(request.Body);
                    Assert.Equal(Value(request) == 42 ? "meter" : ConfigurationInterruptionProcessHost.SlowDeviceKey,
                        json.RootElement.GetProperty("deviceKey").GetString());
                }
                recoveredValues.Add(Value(request));
                await AckAsync(collected.GetStream());
            }
            output.WriteLine($"恢复新采样：值 {string.Join(",", recoveredValues.Order())}，sourceMessageId 均不在强杀前已提交集合。");
            var running = await api.GetJsonAsync(Api + "/runtime/status", Token);
            Assert.Equal("running", Input(running).GetProperty("state").GetString());
            if (partialStop)
            { Assert.Equal("running", Input(running, ConfigurationInterruptionProcessHost.SlowUnitKey).GetProperty("state").GetString()); }
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
                Assert.True(value == draftValue || oldValues.Contains(value));
                await AckAsync(next.GetStream());
                if (value == draftValue)
                { break; }
            }
            output.WriteLine($"显式重新预检并应用：r3，实际值 {draftValue}；中断的 r2 未复用。");
            await restarted.RequestStopAsync(Token);
            Assert.Equal(HostExitCodes.Success, await restarted.WaitForExitAsync(Deadline, Token));
            Assert.Null(state.Pointers().Running);
            Assert.Equal(RuntimeSnapshotStates.NotEffective, state.Snapshot(2).State);
        }
        finally { output.WriteLine(restarted.Output); }
    }

    [Theory]
    [Trait("Category", "Process")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KillAfterConfigurationCommitKeepsCommittedVersionAndIgnoresLaterDraft(bool beforeInputs)
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
        string appliedPath;
        WireRequest frozen;
        DeliveryRow stored;
        DeliveryRow[] committed;

        await using (var host = beforeInputs ? HostProcess.StartPreInputActivationProbe(work.Path, data.Path, DeploymentArguments(port))
            : Start(work.Path, data.Path, port))
        {
            try
            {
                using var api = await ClientAsync(host, setup: true);
                var draft = await CreateDraftAsync(api, port);
                deviceId = draft.DeviceId;
                var initial = await BeginApplyAsync(api, draft.Version);
                await WaitForAsync(api, initial, operation => operation.GetProperty("stage").GetString() == "completed");
                var initialStatus = await api.GetJsonAsync(Api + "/runtime/status", Token);
                var oldSender = Assert.Single(initialStatus.GetProperty("runtime").GetProperty("units").EnumerateArray(),
                    unit => unit.GetProperty("id").GetProperty("kind").GetString() == "output.http").GetProperty("id");
                var oldSenderIdentity = $"{oldSender.GetProperty("kind").GetString()}/{oldSender.GetProperty("key").GetString()}";
                using var initialPeer = await AcceptAsync(receiver);
                var initialRequest = await ReadRequestAsync(initialPeer.GetStream());
                Assert.Equal(42, Value(initialRequest));
                if (!beforeInputs)
                { await AckAsync(initialPeer.GetStream()); }
                var nextVersion = await EditSampleAsync(api, deviceId, draft.InputId, draft.Version, 84);
                if (beforeInputs)
                { File.WriteAllText(Path.Combine(work.Path, ConfigurationInterruptionProcessHost.ActivationArmedFile), "armed"); }
                appliedPath = await BeginApplyAsync(api, nextVersion);
                if (beforeInputs)
                {
                    await WaitForGateAsync(work.Path, ConfigurationInterruptionProcessHost.ActivationEnteredFile,
                        oldSenderIdentity);
                    var applying = await api.GetJsonAsync(appliedPath, Token);
                    Assert.Equal("activating", applying.GetProperty("stage").GetString());
                    Assert.True(applying.GetProperty("committed").GetBoolean());
                    Assert.Equal(2, applying.GetProperty("revision").GetInt64());
                    Assert.False(File.Exists(Path.Combine(work.Path, ConfigurationInterruptionProcessHost.ActivationReleasedFile)));
                    var quiet = await api.GetJsonAsync(Api + "/runtime/status", Token);
                    Assert.Equal(2, quiet.GetProperty("runtime").GetProperty("activeRevision").GetInt64());
                    Assert.Equal("stopped", Input(quiet).GetProperty("state").GetString());
                    Assert.Equal("stopped", Assert.Single(quiet.GetProperty("runtime").GetProperty("units").EnumerateArray(),
                        unit => unit.GetProperty("id").GetProperty("kind").GetString() == "delivery.dispatcher").GetProperty("state").GetString());
                    Assert.Equal("running", Assert.Single(quiet.GetProperty("runtime").GetProperty("units").EnumerateArray(),
                        unit => unit.GetProperty("id").GetProperty("kind").GetString() == "output.http").GetProperty("state").GetString());
                    Assert.Equal(0, quiet.GetProperty("inFlight").GetProperty("reservedBytes").GetInt64());
                    frozen = initialRequest;
                    committed = ReadDeliveries(data.Path);
                    stored = committed[0];
                    Assert.All(committed, row =>
                    {
                        Assert.Equal(1, row.Revision);
                        using var json = JsonDocument.Parse(row.Payload);
                        Assert.Equal(42, json.RootElement.GetProperty("values").GetProperty("value").GetInt64());
                        Assert.Equal(row.SourceId, json.RootElement.GetProperty("sourceMessageId").GetString());
                    });
                    Assert.Equal(stored.SourceId, SourceMessageId(frozen));
                    Assert.Equal(stored.Payload, Encoding.UTF8.GetString(frozen.Body));
                    Assert.Contains($"Idempotency-Key: {stored.Id}", frozen.Header, StringComparison.OrdinalIgnoreCase);
                    Assert.False(receiver.Pending());
                    dirtyVersion = await EditSampleAsync(api, deviceId, draft.InputId, nextVersion, 126);
                    Assert.Equal((2L, (long?)1, (long?)null, (long?)2),
                        (state.Pointers().Effective, state.Pointers().Previous, state.Pointers().Pending, state.Pointers().Running));
                    Assert.Equal(RuntimeSnapshotStates.History, state.Snapshot(1).State);
                    Assert.Equal(RuntimeSnapshotStates.Effective, state.Snapshot(2).State);
                    Assert.False(state.Snapshot(2).IsStable);
                    Assert.Equal(["r1", "r2"], state.Audit(AuditActions.SnapshotApplied).Select(entry => entry.Target).ToArray());
                    // 输入和分发已停，HTTP 队头尚未确认；另持写锁固定已提交集合到强杀完成。
                    using var connection = OpenQueue(data.Path);
                    using var held = connection.BeginTransaction(deferred: false);
                    Assert.Equal(committed, ReadDeliveries(data.Path));
                    output.WriteLine($"强杀前：配置提交已返回，stage=activating/committed=true，新输入尚未启动，旧输入/分发 stopped，旧发送者 running，{state.Pointers()}，冻结 {committed.Length} 条 r1/42。");
                    await host.KillAsync(Token); // 不释放发送者交接判断，不让新输入生成任何投递。
                    held.Rollback();
                }
                else
                {
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
                        Assert.Equal(stored.SourceId, SourceMessageId(frozen));
                        Assert.Equal(stored.Payload, Encoding.UTF8.GetString(frozen.Body));
                        Assert.Contains($"Idempotency-Key: {stored.Id}", frozen.Header, StringComparison.OrdinalIgnoreCase);
                        var completed = await WaitForAsync(api, appliedPath, operation => operation.GetProperty("stage").GetString() == "completed");
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
                    committed = [stored];
                }
                Assert.Equal(committed, ReadDeliveries(data.Path));
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
            Assert.Empty((await api.GetJsonAsync(Api + "/configuration/operations/", Token)).EnumerateArray());
            using var missing = await api.GetAsync(new Uri(appliedPath, UriKind.Relative), Token);
            await ProblemAssert.HasStatusAsync(missing, HttpStatusCode.NotFound, Token);
            using (var replay = await AcceptAsync(receiver))
            {
                var request = await ReadRequestAsync(replay.GetStream());
                Assert.Equal(frozen.Body, request.Body);
                Assert.Contains($"Idempotency-Key: {stored.Id}", request.Header, StringComparison.OrdinalIgnoreCase);
                await AckAsync(replay.GetStream());
            }
            foreach (var old in committed.Skip(1))
            {
                using var replay = await AcceptAsync(receiver);
                var request = await ReadRequestAsync(replay.GetStream());
                Assert.Equal(Encoding.UTF8.GetBytes(old.Payload), request.Body);
                Assert.Contains($"Idempotency-Key: {old.Id}", request.Header, StringComparison.OrdinalIgnoreCase);
                await AckAsync(replay.GetStream());
            }
            using (var collected = await AcceptAsync(receiver))
            {
                var request = await ReadRequestAsync(collected.GetStream());
                Assert.Equal(84, Value(request)); // 不加载 r1，也不采集未应用草稿 126。
                Assert.DoesNotContain(committed, row => row.SourceId == SourceMessageId(request));
                await AckAsync(collected.GetStream());
            }
            output.WriteLine($"恢复新采样：值 84，sourceMessageId 不在强杀前已提交集合；提交后输入未启动窗口={beforeInputs}，草稿 126 未被应用。");
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

    [Theory]
    [Trait("Category", "Process")]
    [InlineData("preparing")]
    [InlineData("partial-stop")]
    [InlineData("pre-input-activation")]
    [InlineData("transaction-writing")]
    public async Task ControlledProbeRunsRealSimulatorsAfterReleaseAndStopsNormally(string stage)
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "受控配置中断与 SIGTERM 子进程只在 Linux 验证。");
        using var work = TemporaryDirectory.Create();
        using var data = TemporaryDirectory.Create();
        using var receiver = new TcpListener(IPAddress.Loopback, 0);
        receiver.Start();
        var port = ((IPEndPoint)receiver.LocalEndpoint).Port;
        var state = new RuntimeStateReader(data.Path);
        var partialStop = stage == "partial-stop";
        var preInputActivation = stage == "pre-input-activation";
        var transactionWriting = stage == "transaction-writing";
        await using var host = stage switch
        {
            "partial-stop" => HostProcess.StartPartialStopProbe(work.Path, data.Path, DeploymentArguments(port)),
            "pre-input-activation" => HostProcess.StartPreInputActivationProbe(work.Path, data.Path, DeploymentArguments(port)),
            "transaction-writing" => HostProcess.StartTransactionWritingProbe(work.Path, data.Path, DeploymentArguments(port)),
            _ => HostProcess.StartPreparationProbe(work.Path, data.Path, DeploymentArguments(port)),
        };
        try
        {
            using var api = await ClientAsync(host, setup: true);
            var draft = await CreateDraftAsync(api, port, partialStop);
            var initial = await BeginApplyAsync(api, draft.Version);
            await WaitForAsync(api, initial, operation => operation.GetProperty("stage").GetString() == "completed");
            var initialStatus = await api.GetJsonAsync(Api + "/runtime/status", Token);
            var oldSender = Assert.Single(initialStatus.GetProperty("runtime").GetProperty("units").EnumerateArray(),
                unit => unit.GetProperty("id").GetProperty("kind").GetString() == "output.http").GetProperty("id");
            var oldSenderIdentity = $"{oldSender.GetProperty("kind").GetString()}/{oldSender.GetProperty("key").GetString()}";
            if (partialStop)
            {
                await WaitForAsync(api, Api + "/runtime/status", status =>
                    Input(status).GetProperty("reads").GetProperty("readsCompleted").GetInt64() >= 1 &&
                    Input(status, ConfigurationInterruptionProcessHost.SlowUnitKey).GetProperty("reads").GetProperty("readsCompleted").GetInt64() >= 1);
            }
            using (var first = await AcceptAsync(receiver))
            {
                var value = Value(await ReadRequestAsync(first.GetStream()));
                Assert.True(value == 42 || partialStop && value == 43);
                await AckAsync(first.GetStream());
            }
            var edited = await EditSampleAsync(api, draft.DeviceId, draft.InputId, draft.Version, 84);
            var armed = transactionWriting ? ConfigurationTransactionGate.ArmedFile : preInputActivation ? ConfigurationInterruptionProcessHost.ActivationArmedFile : partialStop ? ConfigurationInterruptionProcessHost.StopArmedFile : ConfigurationInterruptionProcessHost.ArmedFile;
            var entered = preInputActivation ? ConfigurationInterruptionProcessHost.ActivationEnteredFile : partialStop ? ConfigurationInterruptionProcessHost.StopEnteredFile : ConfigurationInterruptionProcessHost.EnteredFile;
            var released = transactionWriting ? ConfigurationTransactionGate.ReleasedFile : preInputActivation ? ConfigurationInterruptionProcessHost.ActivationReleasedFile : partialStop ? ConfigurationInterruptionProcessHost.StopReleasedFile : ConfigurationInterruptionProcessHost.ReleasedFile;
            File.WriteAllText(Path.Combine(work.Path, armed), "armed");
            var applying = await BeginApplyAsync(api, edited);
            if (transactionWriting)
            { AssertTransactionWriting(await WaitForTransactionGateAsync(work.Path), data.Path); }
            else
            { await WaitForGateAsync(work.Path, entered, preInputActivation ? oldSenderIdentity : partialStop ? ConfigurationInterruptionProcessHost.SlowIdentity : "input.simulator/meter/sample"); }
            var preparing = await api.GetJsonAsync(applying, Token);
            Assert.Equal(transactionWriting ? "committing" : preInputActivation ? "activating" : partialStop ? "quiescing" : "preparing", preparing.GetProperty("stage").GetString());
            if (transactionWriting)
            { Assert.Equal(JsonValueKind.Null, preparing.GetProperty("committed").ValueKind); }
            else
            { Assert.Equal(preInputActivation, preparing.GetProperty("committed").GetBoolean()); }
            Assert.Equal(preInputActivation ? (2L, (long?)null) : (1L, (long?)2), (state.Pointers().Effective, state.Pointers().Pending));
            Assert.False(File.Exists(Path.Combine(work.Path, released)));
            if (preInputActivation)
            {
                var quiet = await api.GetJsonAsync(Api + "/runtime/status", Token);
                Assert.Equal(2, quiet.GetProperty("runtime").GetProperty("activeRevision").GetInt64());
                Assert.Equal("stopped", Input(quiet).GetProperty("state").GetString());
                Assert.Equal(0, quiet.GetProperty("inFlight").GetProperty("reservedBytes").GetInt64());
            }
            if (transactionWriting)
            {
                Assert.Equal(RuntimeSnapshotStates.Effective, state.Snapshot(1).State);
                Assert.Equal(RuntimeSnapshotStates.Pending, state.Snapshot(2).State);
                Assert.Equal((1L, (long?)null, (long?)2, (long?)1),
                    (state.Pointers().Effective, state.Pointers().Previous, state.Pointers().Pending, state.Pointers().Running));
                Assert.Single(state.Audit(AuditActions.SnapshotApplied));
                var quiet = await api.GetJsonAsync(Api + "/runtime/status", Token);
                Assert.Equal(1, quiet.GetProperty("runtime").GetProperty("activeRevision").GetInt64());
                Assert.Equal("stopped", Input(quiet).GetProperty("state").GetString());
                Assert.Equal(0, quiet.GetProperty("inFlight").GetProperty("reservedBytes").GetInt64());
            }
            if (partialStop)
            {
                var quiet = await WaitForAsync(api, Api + "/runtime/status", status => Input(status).GetProperty("state").GetString() == "stopped");
                AssertPartialStop(quiet);
                Assert.False(File.Exists(Path.Combine(work.Path, ConfigurationInterruptionProcessHost.StopDisposedFile)));
            }
            File.Delete(Path.Combine(work.Path, armed)); // 只释放这次旧实例，不在新配置正常停机时再次武装。
            File.WriteAllText(Path.Combine(work.Path, released), "released");
            var completed = await WaitForAsync(api, applying, operation => operation.GetProperty("stage").GetString() == "completed");
            Assert.True(completed.GetProperty("committed").GetBoolean());
            Assert.Equal((2L, (long?)1, (long?)null), (state.Pointers().Effective, state.Pointers().Previous, state.Pointers().Pending));
            if (partialStop)
            {
                Assert.Equal(ConfigurationInterruptionProcessHost.SlowIdentity,
                    await File.ReadAllTextAsync(Path.Combine(work.Path, ConfigurationInterruptionProcessHost.StopDisposedFile), Token));
                var running = await api.GetJsonAsync(Api + "/runtime/status", Token);
                Assert.Equal("running", Input(running).GetProperty("state").GetString());
                Assert.Equal("running", Input(running, ConfigurationInterruptionProcessHost.SlowUnitKey).GetProperty("state").GetString());
            }
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
            deadline.CancelAfter(Deadline);
            while (true)
            {
                using var next = await receiver.AcceptTcpClientAsync(deadline.Token);
                var request = await ReadRequestAsync(next.GetStream());
                var value = Value(request);
                Assert.True(value is 42 or 84 || partialStop && value == 43);
                await AckAsync(next.GetStream());
                if (value == 84)
                { break; }
            }
            Assert.Contains(transactionWriting ? "TEST TRANSACTION WRITING GATE RELEASED" : preInputActivation ? "TEST PRE-INPUT ACTIVATION GATE RELEASED" : partialStop ? "TEST PARTIAL STOP GATE RELEASED" : "TEST PREPARATION GATE RELEASED", host.Output, StringComparison.Ordinal);
            if (preInputActivation)
            {
                var running = await api.GetJsonAsync(Api + "/runtime/status", Token);
                Assert.Equal("running", Input(running).GetProperty("state").GetString());
                output.WriteLine("提交后输入启动同步点已释放：r2 激活完成，真实模拟输入实际发送 84。");
            }
            if (transactionWriting)
            {
                Assert.Equal(RuntimeSnapshotStates.History, state.Snapshot(1).State);
                Assert.Equal(RuntimeSnapshotStates.Effective, state.Snapshot(2).State);
                Assert.Equal(0, state.Snapshot(2).AbnormalExitCount);
                Assert.Equal(["r1", "r2"], state.Audit(AuditActions.SnapshotApplied).Select(entry => entry.Target).ToArray());
                Assert.Empty(state.Events(RuntimeEventTypes.VersionNotEffective));
                var running = await api.GetJsonAsync(Api + "/runtime/status", Token);
                Assert.Equal("running", Input(running).GetProperty("state").GetString());
                output.WriteLine("事务写入同步点已释放：真实 COMMIT 返回，r2 激活完成并实际发送 84，正常停止清除标记。");
            }
            await host.RequestStopAsync(Token);
            Assert.Equal(HostExitCodes.Success, await host.WaitForExitAsync(Deadline, Token));
            Assert.Null(state.Pointers().Running);
        }
        finally { output.WriteLine(host.Output); }
    }

    [Theory]
    [Trait("Category", "Process")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PartialInputActivationKeepsCommittedVersionAndFrozenDeliveries(bool interrupt)
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "真实部分输入激活与 SIGKILL 恢复只在 Linux 验证。");
        using var work = TemporaryDirectory.Create();
        using var data = TemporaryDirectory.Create();
        using var receiver = new TcpListener(IPAddress.Loopback, 0);
        receiver.Start();
        var port = ((IPEndPoint)receiver.LocalEndpoint).Port;
        var state = new RuntimeStateReader(data.Path);
        long deviceId, inputId, slowDeviceId, slowInputId;
        string dirtyVersion, applied;
        WireRequest frozen;
        DeliveryRow[] committed;
        await using (var host = HostProcess.StartPartialActivationProbe(work.Path, data.Path, DeploymentArguments(port)))
        {
            try
            {
                using var api = await ClientAsync(host, setup: true);
                var draft = await CreateDraftAsync(api, port, partialStop: true);
                deviceId = draft.DeviceId;
                inputId = draft.InputId;
                slowDeviceId = draft.SlowDeviceId!.Value;
                var slow = await api.GetJsonAsync($"{Api}/devices/{slowDeviceId}/inputs", Token);
                slowInputId = slow.GetProperty("inputs")[0].GetProperty("id").GetInt64();
                var initial = await BeginApplyAsync(api, draft.Version);
                await WaitForAsync(api, initial, operation => operation.GetProperty("stage").GetString() == "completed");
                using var peer = await AcceptAsync(receiver);
                frozen = await ReadRequestAsync(peer.GetStream()); // 保留未确认的旧 HTTP 队头。
                await WaitForAsync(api, Api + "/runtime/status", _ => ReadDeliveries(data.Path).Select(row =>
                {
                    using var json = JsonDocument.Parse(row.Payload);
                    return json.RootElement.GetProperty("values").GetProperty("value").GetInt64();
                }).Distinct().Count() == 2);
                var edited = await EditSampleAsync(api, deviceId, inputId, draft.Version, 84);
                edited = await EditSampleAsync(api, slowDeviceId, slowInputId, edited, 86, ConfigurationInterruptionProcessHost.SlowInputKey);
                File.WriteAllText(Path.Combine(work.Path, PartialActivationControl.ArmedFile), "armed");
                applied = await BeginApplyAsync(api, edited);
                await WaitForGateAsync(work.Path, PartialActivationControl.InputEnteredFile, ConfigurationInterruptionProcessHost.SlowIdentity);
                await WaitForGateAsync(work.Path, PartialActivationControl.LifecycleEnteredFile, PartialActivationControl.EventType);
                var quiet = await WaitForAsync(api, Api + "/runtime/status", status =>
                    Input(status).GetProperty("reads").GetProperty("readsCompleted").GetInt64() >= 1 &&
                    ReadDeliveries(data.Path).Any(row => row.Revision == 2) &&
                    status.GetProperty("inFlight").GetProperty("reservedBytes").GetInt64() == 0);
                var operation = await api.GetJsonAsync(applied, Token);
                Assert.Equal("activating", operation.GetProperty("stage").GetString());
                Assert.True(operation.GetProperty("committed").GetBoolean());
                Assert.Equal(2, quiet.GetProperty("runtime").GetProperty("activeRevision").GetInt64());
                Assert.Equal("running", Input(quiet).GetProperty("state").GetString());
                Assert.Equal("running", Input(quiet, ConfigurationInterruptionProcessHost.SlowUnitKey).GetProperty("state").GetString());
                Assert.Equal(0, Input(quiet, ConfigurationInterruptionProcessHost.SlowUnitKey).GetProperty("reads").GetProperty("readsCompleted").GetInt64());
                Assert.False(File.Exists(Path.Combine(work.Path, PartialActivationControl.InputReleasedFile)));
                Assert.False(File.Exists(Path.Combine(work.Path, PartialActivationControl.LifecycleReleasedFile)));
                Assert.Equal((2L, (long?)1, (long?)null, (long?)2),
                    (state.Pointers().Effective, state.Pointers().Previous, state.Pointers().Pending, state.Pointers().Running));
                Assert.Equal(RuntimeSnapshotStates.History, state.Snapshot(1).State);
                Assert.Equal(RuntimeSnapshotStates.Effective, state.Snapshot(2).State);
                Assert.False(state.Snapshot(2).IsStable);
                Assert.Contains(state.Events(PartialActivationControl.EventType), entry => entry.Message.Contains("r2", StringComparison.Ordinal));
                dirtyVersion = await EditSampleAsync(api, deviceId, inputId, edited, 126);
                dirtyVersion = await EditSampleAsync(api, slowDeviceId, slowInputId, dirtyVersion, 129, ConfigurationInterruptionProcessHost.SlowInputKey);
                using var connection = OpenQueue(data.Path);
                using var held = connection.BeginTransaction(deferred: false);
                committed = ReadDeliveries(data.Path);
                Assert.Equal(frozen.Body, Encoding.UTF8.GetBytes(committed[0].Payload));
                Assert.Equal(committed[0].SourceId, SourceMessageId(frozen));
                Assert.Contains($"Idempotency-Key: {committed[0].Id}", frozen.Header, StringComparison.OrdinalIgnoreCase);
                Assert.All(committed, row =>
                {
                    using var json = JsonDocument.Parse(row.Payload);
                    var value = json.RootElement.GetProperty("values").GetProperty("value").GetInt64();
                    Assert.Equal(row.SourceId, json.RootElement.GetProperty("sourceMessageId").GetString());
                    if (row.Revision == 1)
                    { Assert.True(value is 42 or 43); }
                    else
                    {
                        Assert.Equal(2, row.Revision);
                        Assert.Equal(84, value);
                        Assert.Equal("meter", json.RootElement.GetProperty("deviceKey").GetString());
                    }
                });
                Assert.Single(committed, row => row.Revision == 2);
                Assert.Equal(["r1", "r2"], state.Audit(AuditActions.SnapshotApplied).Select(entry => entry.Target).ToArray());
                Assert.False(receiver.Pending());
                output.WriteLine($"部分激活窗口：activating/committed=true，r2 快输入已冻结 84，慢输入已受监督但正式循环尚未调用、reads=0；{state.Pointers()}，队列 {committed.Length} 条含旧 r1 与新 r2，草稿 126/129 未应用。");
                if (!interrupt)
                {
                    held.Rollback();
                    File.Delete(Path.Combine(work.Path, PartialActivationControl.ArmedFile));
                    File.WriteAllText(Path.Combine(work.Path, PartialActivationControl.InputReleasedFile), "released");
                    File.WriteAllText(Path.Combine(work.Path, PartialActivationControl.LifecycleReleasedFile), "released");
                    var completed = await WaitForAsync(api, applied, operation => operation.GetProperty("stage").GetString() == "completed");
                    Assert.True(completed.GetProperty("committed").GetBoolean());
                    await AckAsync(peer.GetStream());
                    foreach (var row in committed.Skip(1))
                    {
                        using var delivered = await AcceptAsync(receiver);
                        var request = await ReadRequestAsync(delivered.GetStream());
                        Assert.Equal(Encoding.UTF8.GetBytes(row.Payload), request.Body);
                        Assert.Equal(row.SourceId, SourceMessageId(request));
                        Assert.Contains($"Idempotency-Key: {row.Id}", request.Header, StringComparison.OrdinalIgnoreCase);
                        await AckAsync(delivered.GetStream());
                    }
                    using var slowPeer = await AcceptAsync(receiver);
                    var sample = await ReadRequestAsync(slowPeer.GetStream());
                    Assert.Equal(86, Value(sample));
                    Assert.DoesNotContain(committed, row => row.SourceId == SourceMessageId(sample));
                    using var json = JsonDocument.Parse(sample.Body);
                    Assert.Equal(ConfigurationInterruptionProcessHost.SlowDeviceKey, json.RootElement.GetProperty("deviceKey").GetString());
                    await AckAsync(slowPeer.GetStream());
                    var running = await api.GetJsonAsync(Api + "/runtime/status", Token);
                    Assert.Equal("running", Input(running).GetProperty("state").GetString());
                    Assert.Equal("running", Input(running, ConfigurationInterruptionProcessHost.SlowUnitKey).GetProperty("state").GetString());
                    Assert.True(Input(running, ConfigurationInterruptionProcessHost.SlowUnitKey).GetProperty("reads").GetProperty("readsCompleted").GetInt64() >= 1);
                    Assert.Equal(0, state.Snapshot(2).AbnormalExitCount);
                    Assert.Contains("TEST PARTIAL ACTIVATION INPUT RELEASED", host.Output, StringComparison.Ordinal);
                    Assert.Contains("TEST PARTIAL ACTIVATION LIFECYCLE RELEASED", host.Output, StringComparison.Ordinal);
                    await host.RequestStopAsync(Token);
                    Assert.Equal(HostExitCodes.Success, await host.WaitForExitAsync(Deadline, Token));
                    Assert.Null(state.Pointers().Running);
                    Assert.Equal(2, state.Snapshots().Count);
                    Assert.Empty(state.Events(RuntimeEventTypes.VersionNotEffective));
                    output.WriteLine("部分激活正常释放：原快输入冻结 84 经 HTTP 确认，原慢输入实际采样发送 86，双路 running；SIGTERM=0、运行标记清除、未生成新版本。");
                    return;
                }
                await host.KillAsync(Token);
                held.Rollback();
                Assert.Equal(committed, ReadDeliveries(data.Path));
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
            Assert.Empty(state.Events(RuntimeEventTypes.VersionNotEffective));
            Assert.Empty(state.Audit(AuditActions.AutoRollback));
            Assert.Equal(["r1", "r2"], state.Audit(AuditActions.SnapshotApplied).Select(entry => entry.Target).ToArray());
            foreach (var (id, sample) in new[] { (deviceId, "{\"value\":126}"), (slowDeviceId, "{\"value\":129}") })
            {
                var sources = await api.GetJsonAsync($"{Api}/devices/{id}/inputs", Token);
                Assert.Equal(dirtyVersion, sources.GetProperty("version").GetString());
                Assert.Equal(sample, sources.GetProperty("inputs")[0].GetProperty("simulator").GetProperty("sampleJson").GetString());
            }
            Assert.Empty((await api.GetJsonAsync(Api + "/configuration/operations/", Token)).EnumerateArray());
            using var missing = await api.GetAsync(new Uri(applied, UriKind.Relative), Token);
            await ProblemAssert.HasStatusAsync(missing, HttpStatusCode.NotFound, Token);
            foreach (var row in committed)
            {
                using var replay = await AcceptAsync(receiver);
                var request = await ReadRequestAsync(replay.GetStream());
                Assert.Equal(Encoding.UTF8.GetBytes(row.Payload), request.Body);
                Assert.Equal(row.SourceId, SourceMessageId(request));
                Assert.Contains($"Idempotency-Key: {row.Id}", request.Header, StringComparison.OrdinalIgnoreCase);
                await AckAsync(replay.GetStream());
            }
            var values = new HashSet<long>();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
            deadline.CancelAfter(Deadline);
            while (values.Count < 2)
            {
                using var collected = await receiver.AcceptTcpClientAsync(deadline.Token);
                var request = await ReadRequestAsync(collected.GetStream());
                var value = Value(request);
                Assert.True(value is 84 or 86);
                Assert.DoesNotContain(committed, row => row.SourceId == SourceMessageId(request));
                using var json = JsonDocument.Parse(request.Body);
                Assert.Equal(value == 84 ? "meter" : ConfigurationInterruptionProcessHost.SlowDeviceKey, json.RootElement.GetProperty("deviceKey").GetString());
                values.Add(value);
                await AckAsync(collected.GetStream());
            }
            output.WriteLine("部分激活恢复：旧 r1 和新 r2 冻结记录按 FIFO 原样补发，两路实际新采样 84/86，源编号均不在旧集合；草稿 126/129 未应用。");
            await restarted.RequestStopAsync(Token);
            Assert.Equal(HostExitCodes.Success, await restarted.WaitForExitAsync(Deadline, Token));
            Assert.Null(state.Pointers().Running);
        }
        finally { output.WriteLine(restarted.Output); }
    }

    private static Task WaitForPreparationGateAsync(string directory) =>
        WaitForGateAsync(directory, ConfigurationInterruptionProcessHost.EnteredFile, "input.simulator/meter/sample");

    private static async Task<JsonElement> WaitForTransactionGateAsync(string directory)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
        deadline.CancelAfter(Deadline);
        var path = Path.Combine(directory, ConfigurationTransactionGate.EnteredFile);
        while (!File.Exists(path))
        { await Task.Delay(10, deadline.Token); }
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path, deadline.Token));
        return document.RootElement.Clone();
    }

    private static void AssertTransactionWriting(JsonElement writing, string data)
    {
        Assert.Equal(Path.Combine(data, ConfigDatabase.FileName), writing.GetProperty("database").GetString());
        Assert.True(writing.GetProperty("inTransaction").GetBoolean());
        Assert.Equal(2, writing.GetProperty("effective").GetInt64());
        Assert.Equal(1, writing.GetProperty("previous").GetInt64());
        Assert.Equal(JsonValueKind.Null, writing.GetProperty("pending").ValueKind);
        Assert.Equal(2, writing.GetProperty("running").GetInt64());
        Assert.Equal(RuntimeSnapshotStates.History, writing.GetProperty("oldState").GetString());
        Assert.Equal(RuntimeSnapshotStates.Effective, writing.GetProperty("newState").GetString());
        Assert.Equal("wal", writing.GetProperty("journalMode").GetString());
        Assert.Equal(2, writing.GetProperty("synchronous").GetInt64());
        Assert.Equal(5000, writing.GetProperty("busyTimeout").GetInt64());
    }

    private static async Task WaitForGateAsync(string directory, string file, string identity)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
        deadline.CancelAfter(Deadline);
        var path = Path.Combine(directory, file);
        while (!File.Exists(path))
        { await Task.Delay(10, deadline.Token); }
        // 读到完整身份才算子进程已到达同步点，不能把 Create/Write 之间的短窗口当作已进入。
        while (await File.ReadAllTextAsync(path, deadline.Token) != identity)
        { await Task.Delay(10, deadline.Token); }
    }

    private static async Task<string> EditSampleAsync(HttpClient api, long deviceId, long inputId, string version, int value, string key = "sample")
    {
        using var edit = await api.PutAsJsonAsync(new Uri($"{Api}/devices/{deviceId}/inputs/{inputId}", UriKind.Relative), new
        {
            expectedVersion = version,
            key,
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

    private static async Task<(long DeviceId, long InputId, string Version, long? SlowDeviceId)> CreateDraftAsync(HttpClient api, int port, bool partialStop = false)
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
        var version = input.GetProperty("version").GetString();
        var sourceIds = new List<long> { inputId };
        long? slowDeviceId = null;
        if (partialStop)
        {
            // 同一设备的来源字段不能重名；使用第二台逻辑设备，不绕过产品字段唯一性校验。
            var slowDevice = await PostAsync(api, Api + "/devices", new
            { expectedVersion = version, key = ConfigurationInterruptionProcessHost.SlowDeviceKey, name = "Slow meter", kind = "simulator" });
            slowDeviceId = slowDevice.GetProperty("device").GetProperty("id").GetInt64();
            var slow = await PostAsync(api, $"{Api}/devices/{slowDeviceId}/inputs", new
            {
                expectedVersion = slowDevice.GetProperty("version").GetString(),
                key = ConfigurationInterruptionProcessHost.SlowInputKey,
                name = "Slow stop",
                simulator = new { periodMs = 2000, sampleJson = "{\"value\":43}" },
            });
            sourceIds.Add(slow.GetProperty("input").GetProperty("id").GetInt64());
            version = slow.GetProperty("version").GetString();
        }
        var connection = await PostAsync(api, Api + "/connections", new
        {
            expectedVersion = version,
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
                    new { id = "in", type = "input.simulator", version = 1, name = "Input", config = new { sourceIds } },
                    new { id = "out", type = "output.http", version = 1, name = "Output", config = new { connectionId = connection.GetProperty("connection").GetProperty("id").GetInt64() } },
                },
                edges = new[] { new { source = "in", target = "out" } },
                layout = new { nodes = new Dictionary<string, object>() },
            },
        });
        return (deviceId, inputId, route.GetProperty("version").GetString()!, slowDeviceId);
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

    private static JsonElement Input(JsonElement status, string key = "meter/sample") => Assert.Single(status.GetProperty("runtime").GetProperty("units").EnumerateArray(),
        unit => unit.GetProperty("id").GetProperty("kind").GetString() == "input.simulator" &&
            unit.GetProperty("id").GetProperty("key").GetString() == key);

    private static void AssertPartialStop(JsonElement status)
    {
        Assert.Equal("stopped", Input(status).GetProperty("state").GetString());
        Assert.Equal("stopping", Input(status, ConfigurationInterruptionProcessHost.SlowUnitKey).GetProperty("state").GetString());
        Assert.Equal(1, status.GetProperty("runtime").GetProperty("activeRevision").GetInt64());
        Assert.Equal(2, status.GetProperty("runtime").GetProperty("units").EnumerateArray()
            .Count(unit => unit.GetProperty("id").GetProperty("kind").GetString() == "input.simulator"));
    }

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
    private static string SourceMessageId(WireRequest request) { using var json = JsonDocument.Parse(request.Body); return json.RootElement.GetProperty("sourceMessageId").GetString()!; }
    private sealed record WireRequest(string Header, byte[] Body);
    private sealed record DeliveryRow(long Seq, string Id, long Revision, string SourceId, string Payload);
}
