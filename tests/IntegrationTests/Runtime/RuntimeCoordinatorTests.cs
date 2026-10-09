using System.Net;
using Microsoft.Extensions.DependencyInjection;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Plans;
using TenonEdge.Host.Features.Audit;
using TenonEdge.Host.Features.Health;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Storage;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.Runtime.Deliveries;
using TenonEdge.Runtime.Lifecycle;

namespace TenonEdge.IntegrationTests.Runtime;

/// <summary>
/// 运行时协调者（T1-01；总设计 §7.4、§8.1，D-07）：种子快照按“冻结、准备、提交”成为有效版本，准备失败的版本标为未生效且不影响原版本；
/// 版本元数据（三个指针、“运行中”标记、稳定与非正常退出计数）在配置库中正确维护；启动保护按判定自动回滚一次或进入“运行失败”；
/// 有效快照损坏与提交前中断按中断点表处理。非正常退出在这里通过改写“运行中”标记模拟，真实进程被强制终止的验证见
/// <c>StartupProtectionProcessTests</c>。
/// </summary>
public sealed class RuntimeCoordinatorTests
{
    private const string MeterA = """{"kind":"input.simulator","key":"meter-001/simulated","configuration":{"periodMs":1000,"seed":42}}""";
    private const string MeterB = """{"kind":"input.simulator","key":"meter-001/simulated","configuration":{"periodMs":2000,"seed":42}}""";
    private const string MissingModule = """{"kind":"input.unregistered","key":"meter-002/registers","configuration":{}}""";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private const string SimulatorUnit = "input.simulator/meter-001/simulated";

    [Fact]
    public async Task WithoutASeedTheRuntimeIsIdleAndReady()
    {
        using var data = TemporaryDirectory.Create();
        await using (var factory = new FixedDataDirectoryApplicationFactory(data.Path))
        {
            Assert.Equal(HttpStatusCode.OK, await ReadinessAsync(factory));
            Assert.Equal(RuntimeCoordinatorState.Idle, Coordinator(factory).Status.State);
            await WaitUntilAsync(() => File.Exists(Path.Combine(data.Path, DeliveryCacheOptions.FileName)));
        }

        var state = new RuntimeStateReader(data.Path);
        Assert.Null(state.Pointers().Effective);
        Assert.Empty(state.Snapshots());
    }

    [Fact]
    public async Task ASeedBecomesTheEffectiveVersionAndItsInputsRun()
    {
        using var data = TemporaryDirectory.Create();
        using var seeds = TemporaryDirectory.Create();
        var seed = WriteSeed(seeds.Path, MeterA);
        var state = new RuntimeStateReader(data.Path);

        await using (var factory = Start(data.Path, seed))
        {
            Assert.Equal(HttpStatusCode.OK, await ReadinessAsync(factory));
            Assert.Equal(new RuntimeCoordinatorStatus(RuntimeCoordinatorState.Active, null, 1), Coordinator(factory).Status);
            var runtime = factory.Services.GetRequiredService<RuntimeHost>();
            await WaitUntilAsync(() => runtime.GetStatus().Units.Any(unit => unit.Id.ToString() == SimulatorUnit && unit.Reads!.ReadsCompleted >= 1));
            Assert.Equal(RuntimeUnitState.Running, runtime.GetStatus().Units.Single(unit => unit.Id.ToString() == SimulatorUnit).State);

            var pointers = state.Pointers();
            var snapshot = state.Snapshot(1);
            Assert.Equal((1L, (long?)null, (long?)null, (long?)1), (pointers.Effective!.Value, pointers.Previous, pointers.Pending, pointers.Running));
            Assert.Equal(snapshot.ContentHash, pointers.SeedHash);
            Assert.Equal((RuntimeSnapshotOrigins.Seed, RuntimeSnapshotStates.Effective, RuntimeVersionStore.SystemOperator), (snapshot.Origin, snapshot.State, snapshot.CreatedBy));
            Assert.Equal((false, 0), (snapshot.IsStable, snapshot.AbnormalExitCount));
            var audit = Assert.Single(state.Audit(AuditActions.SeedSnapshotApplied));
            Assert.Equal((RuntimeVersionStore.SystemOperator, "r1", AuditResults.Success), (audit.Account, audit.Target, audit.Result));
            Assert.Single(state.Events(RuntimeEventTypes.SeedApplied));
        }

        // 按停机顺序正常退出：清除“运行中”标记，不计非正常退出。
        Assert.Null(state.Pointers().Running);
        Assert.Equal(0, state.Snapshot(1).AbnormalExitCount);
    }

    [Fact]
    public async Task AnUnchangedSeedIsNotImportedAgainButAChangedOneIs()
    {
        using var data = TemporaryDirectory.Create();
        using var seeds = TemporaryDirectory.Create();
        var seed = WriteSeed(seeds.Path, MeterA);
        var state = new RuntimeStateReader(data.Path);

        await StartAndStopAsync(data.Path, seed);
        await StartAndStopAsync(data.Path, seed);
        Assert.Single(state.Snapshots());

        WriteSeed(seeds.Path, MeterB);
        await using (var factory = Start(data.Path, seed))
        {
            Assert.Equal(2, Coordinator(factory).Status.ActiveRevision);
        }

        var pointers = state.Pointers();
        Assert.Equal((2L, 1L), (pointers.Effective!.Value, pointers.Previous!.Value));
        Assert.Equal([RuntimeSnapshotStates.History, RuntimeSnapshotStates.Effective], state.Snapshots().Select(row => row.State));
    }

    [Fact]
    public async Task ASeedThatFailsToBuildLeavesTheEffectiveVersionInPlace()
    {
        using var data = TemporaryDirectory.Create();
        using var seeds = TemporaryDirectory.Create();
        var seed = WriteSeed(seeds.Path, MeterA);
        var state = new RuntimeStateReader(data.Path);
        await StartAndStopAsync(data.Path, seed);

        WriteSeed(seeds.Path, MeterA, MissingModule);
        await using (var factory = Start(data.Path, seed))
        {
            Assert.Equal(new RuntimeCoordinatorStatus(RuntimeCoordinatorState.Active, null, 1), Coordinator(factory).Status);
        }

        var rejected = state.Snapshot(2);
        Assert.Equal(RuntimeSnapshotStates.NotEffective, rejected.State);
        Assert.Contains("没有注册单元种类 input.unregistered", rejected.NotEffectiveReason, StringComparison.Ordinal);
        Assert.Equal((1L, (long?)null, (long?)null), (state.Pointers().Effective!.Value, state.Pointers().Previous, state.Pointers().Pending));
        Assert.Single(state.Events(RuntimeEventTypes.VersionNotEffective));

        // 同一份种子不再重复导入；版本号不复用。
        await StartAndStopAsync(data.Path, seed);
        Assert.Equal(2, state.Snapshots().Count);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"schemaVersion":1,"units":[{"kind":"input.simulator","key":"meter 1/simulated","configuration":{}}]}""")]
    [InlineData(null)] // 文件不存在
    public async Task AnInvalidSeedCreatesNoVersion(string? content)
    {
        using var data = TemporaryDirectory.Create();
        using var seeds = TemporaryDirectory.Create();
        var seed = Path.Combine(seeds.Path, "seed.json");
        if (content is not null)
        {
            await File.WriteAllTextAsync(seed, content, TestContext.Current.CancellationToken);
        }

        await using (var factory = Start(data.Path, seed))
        {
            Assert.Equal(HttpStatusCode.OK, await ReadinessAsync(factory));
            Assert.Equal(RuntimeCoordinatorState.Idle, Coordinator(factory).Status.State);
        }

        var state = new RuntimeStateReader(data.Path);
        Assert.Empty(state.Snapshots());
        Assert.Null(state.Pointers().SeedHash);
        Assert.Equal("error", Assert.Single(state.Events(RuntimeEventTypes.SeedRejected)).Level);
    }

    [Fact]
    public async Task TheVersionIsMarkedStableAfterTheStableTime()
    {
        using var data = TemporaryDirectory.Create();
        using var seeds = TemporaryDirectory.Create();
        var state = new RuntimeStateReader(data.Path);

        await using var factory = Start(data.Path, WriteSeed(seeds.Path, MeterA), stableAfterSeconds: 1);
        _ = Coordinator(factory).Status;
        // 先记“已稳定”，随后另一个事务写运行事件。
        await state.WaitUntilAsync(reader => reader.Snapshot(1).IsStable && reader.Events(RuntimeEventTypes.VersionStable).Count > 0, Timeout, TestContext.Current.CancellationToken);

        Assert.Single(state.Events(RuntimeEventTypes.VersionStable));
    }

    [Fact]
    public async Task ALeftOverRunningMarkerIsCountedOnlyForAVersionThatNeverBecameStable()
    {
        using var data = TemporaryDirectory.Create();
        using var seeds = TemporaryDirectory.Create();
        var seed = WriteSeed(seeds.Path, MeterA);
        var state = new RuntimeStateReader(data.Path);
        await StartAndStopAsync(data.Path, seed);

        // 上次运行 r1 时进程被强制终止：标记仍在。
        state.Execute("UPDATE system_state SET running_revision = 1;");
        await StartAndStopAsync(data.Path, seed);
        Assert.Equal(1, state.Snapshot(1).AbnormalExitCount);
        Assert.Equal("warning", Assert.Single(state.Events(RuntimeEventTypes.AbnormalExit)).Level);

        // 已稳定的版本上的退出不计入。
        state.Execute("UPDATE runtime_snapshot SET is_stable = 1 WHERE revision = 1; UPDATE system_state SET running_revision = 1;");
        await StartAndStopAsync(data.Path, seed);
        Assert.Equal(1, state.Snapshot(1).AbnormalExitCount);
        Assert.Contains(state.Events(RuntimeEventTypes.AbnormalExit), row => row.Level == "info" && row.Message.Contains("不计入", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReachingTheLimitRollsBackOnceToTheLastStableVersion()
    {
        using var data = TemporaryDirectory.Create();
        using var seeds = TemporaryDirectory.Create();
        var seed = WriteSeed(seeds.Path, MeterA);
        var state = new RuntimeStateReader(data.Path);
        await using (var stable = Start(data.Path, seed, stableAfterSeconds: 1))
        {
            _ = Coordinator(stable).Status;
            await state.WaitUntilAsync(reader => reader.Snapshot(1).IsStable, Timeout, TestContext.Current.CancellationToken);
        }

        WriteSeed(seeds.Path, MeterB);
        await StartAndStopAsync(data.Path, seed);

        // r2 从未稳定，已非正常退出 2 次，第 3 次的标记还在。
        state.Execute("UPDATE runtime_snapshot SET abnormal_exit_count = 2 WHERE revision = 2; UPDATE system_state SET running_revision = 2;");
        await using (var factory = Start(data.Path, seed))
        {
            Assert.Equal(HttpStatusCode.OK, await ReadinessAsync(factory));
            Assert.Equal(new RuntimeCoordinatorStatus(RuntimeCoordinatorState.Active, null, 3), Coordinator(factory).Status);
        }

        var rollback = state.Snapshot(3);
        Assert.Equal((RuntimeSnapshotOrigins.AutoRollback, "r1", RuntimeVersionStore.SystemOperator), (rollback.Origin, rollback.OriginDetail, rollback.CreatedBy));
        Assert.Equal(state.Snapshot(1).Content, rollback.Content);
        Assert.Equal(3, state.Snapshot(2).AbnormalExitCount);
        Assert.Equal((3L, 2L), (state.Pointers().Effective!.Value, state.Pointers().Previous!.Value));
        var audit = Assert.Single(state.Audit(AuditActions.AutoRollback));
        Assert.Equal((RuntimeVersionStore.SystemOperator, "r3"), (audit.Account, audit.Target));
        Assert.Single(state.Events(RuntimeEventTypes.AutoRollback));

        // 种子没有变化，不会在下次启动时把引起回滚的内容再应用一次。
        await StartAndStopAsync(data.Path, seed);
        Assert.Equal(3, state.Pointers().Effective);
    }

    [Fact]
    public async Task WithoutAStablePreviousVersionTheRuntimeFailsButManagementKeepsWorking()
    {
        using var data = TemporaryDirectory.Create();
        using var seeds = TemporaryDirectory.Create();
        var seed = WriteSeed(seeds.Path, MeterA);
        var state = new RuntimeStateReader(data.Path);
        await StartAndStopAsync(data.Path, seed);
        state.Execute("UPDATE runtime_snapshot SET abnormal_exit_count = 2 WHERE revision = 1; UPDATE system_state SET running_revision = 1;");

        await using (var factory = Start(data.Path, seed))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, await ReadinessAsync(factory));
            Assert.Equal(HttpStatusCode.OK, await LivenessAsync(factory));
            var status = Coordinator(factory).Status;
            Assert.Equal(RuntimeCoordinatorState.RunFailed, status.State);
            Assert.Contains("没有上一生效版本", status.Reason, StringComparison.Ordinal);

            // 输入不启动，发送缓存照常运行。
            var units = factory.Services.GetRequiredService<RuntimeHost>().GetStatus().Units;
            Assert.Equal([DeliveryCache.WriterId], units.Select(unit => unit.Id));
            Assert.Null(state.Pointers().Running);
        }

        Assert.Single(state.Events(RuntimeEventTypes.RunFailed));
        Assert.Equal(3, state.Snapshot(1).AbnormalExitCount);
    }

    [Fact]
    public async Task ACorruptEffectiveSnapshotFailsTheRuntimeWithoutClearingTheConfiguration()
    {
        using var data = TemporaryDirectory.Create();
        using var seeds = TemporaryDirectory.Create();
        var seed = WriteSeed(seeds.Path, MeterA);
        var state = new RuntimeStateReader(data.Path);
        await StartAndStopAsync(data.Path, seed);
        state.Execute("UPDATE runtime_snapshot SET content = replace(content, '\"seed\":42', '\"seed\":43') WHERE revision = 1;");
        var tampered = state.Snapshot(1);

        await using (var factory = Start(data.Path, seed))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, await ReadinessAsync(factory));
            Assert.Contains("内容哈希不符", Coordinator(factory).Status.Reason, StringComparison.Ordinal);
        }

        Assert.Equal(tampered, state.Snapshot(1));
        Assert.Equal(1, state.Pointers().Effective);
    }

    [Fact]
    public async Task AVersionInterruptedBeforeCommitIsDiscardedAtStartup()
    {
        using var data = TemporaryDirectory.Create();
        using var seeds = TemporaryDirectory.Create();
        var seed = WriteSeed(seeds.Path, MeterA);
        var state = new RuntimeStateReader(data.Path);
        await StartAndStopAsync(data.Path, seed);

        // 冻结之后、提交之前进程退出：pendingRevision 留在库中。
        state.Execute("""
            INSERT INTO runtime_snapshot (revision, schema_version, content, content_hash, origin, state, created_at, created_by)
            SELECT 2, schema_version, content, content_hash, origin, 'pending', created_at, created_by FROM runtime_snapshot WHERE revision = 1;
            UPDATE system_state SET pending_revision = 2, revision_floor = 2;
            """);
        await StartAndStopAsync(data.Path, seed);

        var discarded = state.Snapshot(2);
        Assert.Equal((RuntimeSnapshotStates.NotEffective, "进程中断"), (discarded.State, discarded.NotEffectiveReason));
        Assert.Equal((1L, (long?)null), (state.Pointers().Effective!.Value, state.Pointers().Pending));
        Assert.Contains(state.Events(RuntimeEventTypes.VersionNotEffective), row => row.Target == "r2");
    }

    [Fact]
    public async Task ASeedImportInterruptedBeforeCommitIsRetriedAtTheNextStart()
    {
        using var data = TemporaryDirectory.Create();
        using var seeds = TemporaryDirectory.Create();
        var seed = WriteSeed(seeds.Path, MeterA);
        var state = new RuntimeStateReader(data.Path);
        var interrupted = RuntimePlanDocument.Parse(System.Text.Encoding.UTF8.GetBytes("{\"schemaVersion\":1,\"units\":[" + MeterB + "]}"));

        // 导入种子 B 时在冻结之后、提交之前中断：版本已冻结为 pending，没有提交。
        await using (var factory = Start(data.Path, seed))
        {
            factory.Services.GetRequiredService<RuntimeVersionStore>()
                .Freeze(interrupted, RuntimeSnapshotOrigins.Seed, interrupted.ContentHash, RuntimeVersionStore.SystemOperator);
        }

        Assert.Equal(2, state.Pointers().Pending);
        Assert.NotEqual(interrupted.ContentHash, state.Pointers().SeedHash);

        // 下次启动：丢弃中断的 r2，同一份种子重新导入为 r3。
        WriteSeed(seeds.Path, MeterB);
        await StartAndStopAsync(data.Path, seed);

        Assert.Equal((RuntimeSnapshotStates.NotEffective, "进程中断"), (state.Snapshot(2).State, state.Snapshot(2).NotEffectiveReason));
        Assert.Equal((3L, interrupted.ContentHash), (state.Pointers().Effective!.Value, state.Pointers().SeedHash));
        Assert.Equal(interrupted.ContentHash, state.Snapshot(3).ContentHash);
    }

    [Fact]
    public async Task AFailingCancellationCallbackDoesNotStopTheShutdownFromClearingTheMarker()
    {
        // 单元在取消回调中释放连接时出错：停机照常完成（写者停止、请求队列关闭），“运行中”标记照常清除，错误记为运行事件。
        using var data = TemporaryDirectory.Create();
        using var seeds = TemporaryDirectory.Create();
        var seed = WriteSeed(seeds.Path, """{"kind":"input.teststop","key":"meter-009/stop","configuration":{}}""");
        var state = new RuntimeStateReader(data.Path);
        RuntimeHost runtime;
        await using (var factory = Start(data.Path, seed, configureServices: services => services.AddSingleton<IInputUnitFactory, FailingStopInputFactory>()))
        {
            Assert.Equal(RuntimeCoordinatorState.Active, Coordinator(factory).Status.State);
            Assert.Equal(1, state.Pointers().Running);
            runtime = factory.Services.GetRequiredService<RuntimeHost>();
        }

        Assert.Null(state.Pointers().Running);
        Assert.All(runtime.GetStatus().Units, unit => Assert.Equal(RuntimeUnitState.Stopped, unit.State));
        var late = await runtime.Deliveries.EnqueueAsync([Draft()], TestContext.Current.CancellationToken).WaitAsync(Timeout, TestContext.Current.CancellationToken);
        Assert.False(late.Committed);
        Assert.Single(state.Events(TenonEdge.Runtime.Diagnostics.RuntimeEventTypes.UnitStopFailed));
    }

    [Fact]
    public async Task TheRunningMarkerIsWrittenBeforeTheEffectiveVersionIsBuilt()
    {
        // 构建运行单元时进程崩溃也要计入：加载有效版本时，工厂构建时“运行中”标记已经落盘。
        using var data = TemporaryDirectory.Create();
        using var seeds = TemporaryDirectory.Create();
        var seed = WriteSeed(seeds.Path, """{"kind":"input.testprobe","key":"meter-009/probe","configuration":{}}""");
        var probe = new MarkerProbeFactory(new RuntimeStateReader(data.Path));
        void Probe(IServiceCollection services) => services.AddSingleton<IInputUnitFactory>(probe);

        // 第一次启动导入种子：构建发生在提交之前，此时它还不是有效版本，没有标记。
        await using (var factory = Start(data.Path, seed, configureServices: Probe))
        {
            _ = Coordinator(factory).Status;
        }

        // 第二次启动加载有效版本 r1：构建时标记已经是 r1。
        await using (var factory = Start(data.Path, seed, configureServices: Probe))
        {
            _ = Coordinator(factory).Status;
        }

        Assert.Equal([null, 1L], probe.MarkersSeenAtBuild);
    }

    [Fact]
    public async Task ASeedWhoseImportKeepsGettingInterruptedIsNoLongerRetried()
    {
        using var data = TemporaryDirectory.Create();
        using var seeds = TemporaryDirectory.Create();
        var seed = WriteSeed(seeds.Path, MeterA);
        var state = new RuntimeStateReader(data.Path);
        await StartAndStopAsync(data.Path, seed);
        var next = RuntimePlanDocument.Parse(System.Text.Encoding.UTF8.GetBytes($$"""{"schemaVersion":1,"units":[{{MeterB}}]}"""));

        // 导入 B 时进程已经 3 次在提交前退出（每次都留下一个“未生效：进程中断”的版本）。
        state.Execute($$"""
            INSERT INTO runtime_snapshot (revision, schema_version, content, content_hash, origin, origin_detail, state, not_effective_reason, created_at, created_by)
            VALUES (2, 1, '{{next.CanonicalText}}', '{{next.ContentHash}}', 'seed', '{{next.ContentHash}}', 'notEffective', '进程中断', 0, '系统'),
                   (3, 1, '{{next.CanonicalText}}', '{{next.ContentHash}}', 'seed', '{{next.ContentHash}}', 'notEffective', '进程中断', 0, '系统'),
                   (4, 1, '{{next.CanonicalText}}', '{{next.ContentHash}}', 'seed', '{{next.ContentHash}}', 'notEffective', '进程中断', 0, '系统');
            UPDATE system_state SET revision_floor = 4;
            """);
        WriteSeed(seeds.Path, MeterB);
        await StartAndStopAsync(data.Path, seed);

        Assert.Equal(4, state.Snapshots().Count);
        Assert.Equal((1L, next.ContentHash), (state.Pointers().Effective!.Value, state.Pointers().SeedHash));
        Assert.Contains(state.Events(RuntimeEventTypes.SeedRejected), row => row.Message.Contains("提交前中断", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OnlyTheLatestConsecutiveInterruptionsOfASeedAreCounted()
    {
        // 只计同一份种子最近一段连续的“进程中断”：之后一次导入因构建失败而未生效，打断了连续，之前的 3 次中断不再计入，种子照常导入。
        using var data = TemporaryDirectory.Create();
        using var seeds = TemporaryDirectory.Create();
        var seed = WriteSeed(seeds.Path, MeterA);
        var state = new RuntimeStateReader(data.Path);
        await StartAndStopAsync(data.Path, seed);
        var next = RuntimePlanDocument.Parse(System.Text.Encoding.UTF8.GetBytes($$"""{"schemaVersion":1,"units":[{{MeterB}}]}"""));

        state.Execute($$"""
            INSERT INTO runtime_snapshot (revision, schema_version, content, content_hash, origin, origin_detail, state, not_effective_reason, created_at, created_by)
            VALUES (2, 1, '{{next.CanonicalText}}', '{{next.ContentHash}}', 'seed', '{{next.ContentHash}}', 'notEffective', '进程中断', 0, '系统'),
                   (3, 1, '{{next.CanonicalText}}', '{{next.ContentHash}}', 'seed', '{{next.ContentHash}}', 'notEffective', '进程中断', 0, '系统'),
                   (4, 1, '{{next.CanonicalText}}', '{{next.ContentHash}}', 'seed', '{{next.ContentHash}}', 'notEffective', '进程中断', 0, '系统'),
                   (5, 1, '{{next.CanonicalText}}', '{{next.ContentHash}}', 'seed', '{{next.ContentHash}}', 'notEffective', '构建失败：模拟', 0, '系统');
            UPDATE system_state SET revision_floor = 5;
            """);
        WriteSeed(seeds.Path, MeterB);
        await StartAndStopAsync(data.Path, seed);

        Assert.Equal((6L, next.ContentHash), (state.Pointers().Effective!.Value, state.Pointers().SeedHash));
        Assert.DoesNotContain(state.Events(RuntimeEventTypes.SeedRejected), row => row.Message.Contains("提交前中断", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnAutomaticRollbackThatKeepsGettingInterruptedEndsInRunFailure()
    {
        using var data = TemporaryDirectory.Create();
        using var seeds = TemporaryDirectory.Create();
        var seed = WriteSeed(seeds.Path, MeterA);
        var state = new RuntimeStateReader(data.Path);
        await StartAndStopAsync(data.Path, seed);
        WriteSeed(seeds.Path, MeterB);
        await StartAndStopAsync(data.Path, seed);
        var target = state.Snapshot(1);

        // r1 曾稳定，r2 已非正常退出 3 次；回滚到 r1 已经 3 次在提交前中断。
        state.Execute($$"""
            UPDATE runtime_snapshot SET is_stable = 1 WHERE revision = 1;
            UPDATE runtime_snapshot SET abnormal_exit_count = 3 WHERE revision = 2;
            INSERT INTO runtime_snapshot (revision, schema_version, content, content_hash, origin, origin_detail, state, not_effective_reason, created_at, created_by)
            VALUES (3, 1, '{{target.Content}}', '{{target.ContentHash}}', 'autoRollback', 'r1', 'notEffective', '进程中断', 0, '系统'),
                   (4, 1, '{{target.Content}}', '{{target.ContentHash}}', 'autoRollback', 'r1', 'notEffective', '进程中断', 0, '系统'),
                   (5, 1, '{{target.Content}}', '{{target.ContentHash}}', 'autoRollback', 'r1', 'notEffective', '进程中断', 0, '系统');
            UPDATE system_state SET revision_floor = 5;
            """);
        await using (var factory = Start(data.Path, seed))
        {
            var status = Coordinator(factory).Status;
            Assert.Equal(RuntimeCoordinatorState.RunFailed, status.State);
            Assert.Contains("提交前中断", status.Reason, StringComparison.Ordinal);
        }

        Assert.Equal(5, state.Snapshots().Count);
        Assert.Equal(2, state.Pointers().Effective);
    }

    private static FixedDataDirectoryApplicationFactory Start(string dataDirectory, string seed, int stableAfterSeconds = 600, Action<IServiceCollection>? configureServices = null) => new(
        dataDirectory,
        new Dictionary<string, string>
        {
            [RuntimeSettings.SeedSnapshotKey] = seed,
            [RuntimeSettings.StableAfterSecondsKey] = stableAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
        },
        configureServices);

    /// <summary>启动一次宿主、等它就绪判定完成后按停机顺序停止。</summary>
    private static async Task StartAndStopAsync(string dataDirectory, string seed)
    {
        await using var factory = Start(dataDirectory, seed);
        _ = Coordinator(factory).Status;
    }

    private static RuntimeCoordinator Coordinator(FixedDataDirectoryApplicationFactory factory) => factory.Services.GetRequiredService<RuntimeCoordinator>();

    private static async Task<HttpStatusCode> ReadinessAsync(FixedDataDirectoryApplicationFactory factory)
    {
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(new Uri(HealthEndpoints.ReadinessPath, UriKind.Relative), TestContext.Current.CancellationToken);
        return response.StatusCode;
    }

    private static async Task<HttpStatusCode> LivenessAsync(FixedDataDirectoryApplicationFactory factory)
    {
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(new Uri(HealthEndpoints.LivenessPath, UriKind.Relative), TestContext.Current.CancellationToken);
        return response.StatusCode;
    }

    private static string WriteSeed(string directory, params string[] units)
    {
        var path = Path.Combine(directory, "seed.json");
        File.WriteAllText(path, $$"""{"schemaVersion":1,"units":[{{string.Join(",", units)}}]}""");
        return path;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("等待条件成立超时。");
            }

            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
    }

    private static DeliveryDraft Draft() =>
        new(Guid.CreateVersion7(), 1, Guid.CreateVersion7(), "meter-009", 1, "{}"u8.ToArray(), "fp", "summary");

    /// <summary>测试用的输入：停止时在取消回调中抛出异常（模拟释放连接失败）。</summary>
    private sealed class FailingStopInputFactory : IInputUnitFactory
    {
        public string Kind => "input.teststop";

        public IRuntimeUnit Build(RuntimeUnitPlan unit, InputUnitContext context) => new FailingStopUnit(unit.Id);

        private sealed class FailingStopUnit(RuntimeUnitId id) : IRuntimeUnit
        {
            public RuntimeUnitId Id => id;

            public async Task RunAsync(CancellationToken stoppingToken)
            {
                await using var release = stoppingToken.Register(() => throw new IOException("释放连接失败"));
                await Task.Delay(System.Threading.Timeout.Infinite, stoppingToken);
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    /// <summary>测试用的输入：构建时读取配置库中的“运行中”标记（只用于验证顺序，正式工厂不得在构建时做 I/O）。</summary>
    private sealed class MarkerProbeFactory(RuntimeStateReader state) : IInputUnitFactory
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<long?> _markers = new();

        public string Kind => "input.testprobe";

        public IReadOnlyList<long?> MarkersSeenAtBuild => [.. _markers];

        public IRuntimeUnit Build(RuntimeUnitPlan unit, InputUnitContext context)
        {
            _markers.Enqueue(state.Pointers().Running);
            return new IdleUnit(unit.Id);
        }

        private sealed class IdleUnit(RuntimeUnitId id) : IRuntimeUnit
        {
            public RuntimeUnitId Id => id;

            public Task RunAsync(CancellationToken stoppingToken) => Task.Delay(System.Threading.Timeout.Infinite, stoppingToken);

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
