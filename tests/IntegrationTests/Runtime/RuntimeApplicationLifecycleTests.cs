using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Abstractions.Plans;
using TenonEdge.Adapters.Simulator;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Devices;
using TenonEdge.Host.Features.Inputs;
using TenonEdge.Host.Features.Routes;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Storage;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.Runtime.Connections;
using TenonEdge.Runtime.Lifecycle;

namespace TenonEdge.IntegrationTests.Runtime;

public sealed class RuntimeApplicationLifecycleTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task RequestCancellationDoesNotReleaseApplicationOwnershipAndShutdownWaitsForIt()
    {
        using var data = TemporaryDirectory.Create();
        using var input = new ControlledInput { BlockPreparation = true };
        await using var factory = Create(data.Path, input);
        var saved = Seed(factory);
        var coordinator = factory.Services.GetRequiredService<RuntimeCoordinator>();
        var versions = factory.Services.GetRequiredService<RuntimeVersionStore>();
        using var cancellation = new CancellationTokenSource();
        var application = coordinator.ApplyDraftAsync(saved.Version, "operator", null, cancellation.Token);
        try
        {
            await input.Preparing.Task.WaitAsync(Deadline, Token);
            Assert.Equal(1, versions.ReadPointers().PendingRevision);
            Assert.Null(versions.ReadPointers().EffectiveRevision);
            await Assert.ThrowsAsync<RuntimeApplicationBusyException>(async () =>
                await coordinator.ApplyDraftAsync(saved.Version, "other", null, Token));
            var drafts = factory.Services.GetRequiredService<RouteDraftStore>();
            var edited = drafts.Write(saved.Route.Id, new(saved.Version, saved.Route.Key, saved.Route.Name, false, saved.Route.Graph), "editor", null);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => application);
            var stopping = coordinator.StopAsync(Token);
            Assert.False(stopping.IsCompleted);
            input.ReleasePreparation.Set();
            await stopping.WaitAsync(Deadline, Token);
            Assert.Equal(1, versions.ReadPointers().EffectiveRevision);
            Assert.Null(versions.ReadPointers().RunningRevision);
            Assert.Null(versions.ReadPointers().PendingRevision);
            Assert.Equal(1, coordinator.Runtime.ActiveRevision);
            Assert.Equal(edited.Version, drafts.Read().Version);
            Assert.NotEmpty(RuntimeSnapshotDocument.ReadPlan(versions.Load(1)!).Units);
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await coordinator.ApplyDraftAsync(edited.Version, "operator", null, Token));
        }
        finally { input.ReleasePreparation.Set(); }
    }

    [Fact]
    public async Task PreparationFailureRecordsSanitizedUnitReasonWithoutStoppingOldInputs()
    {
        using var data = TemporaryDirectory.Create();
        using var input = new ControlledInput();
        await using var factory = Create(data.Path, input);
        var saved = Seed(factory);
        var coordinator = factory.Services.GetRequiredService<RuntimeCoordinator>();
        Assert.True((await coordinator.ApplyDraftAsync(saved.Version, "operator", null, Token)).Activated);
        await OutputPipelineTests.UntilAsync(() => input.Runs == 1);
        input.RejectPreparation = true;
        var rejected = await coordinator.ApplyDraftAsync(saved.Version, "operator", null, Token);
        Assert.False(rejected.Committed);
        Assert.Equal("runtime.prepareFailed", rejected.Code);
        Assert.Equal(1, coordinator.Runtime.ActiveRevision);
        Assert.Equal(1, input.Runs);
        var snapshot = factory.Services.GetRequiredService<RuntimeVersionStore>().Load(rejected.Revision)!;
        Assert.Equal(RuntimeSnapshotStates.NotEffective, snapshot.State);
        Assert.Contains("input.simulator/meter/sample", snapshot.NotEffectiveReason!, StringComparison.Ordinal);
        Assert.DoesNotContain("fixture-sensitive-build-detail", snapshot.NotEffectiveReason!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SavedRouteRunsOnlyAfterApplyAndPendingDataBlocksRemovingItsTarget()
    {
        using var data = TemporaryDirectory.Create();
        using var input = new ControlledInput();
        await using var factory = Create(data.Path, input);
        var saved = Seed(factory);
        var coordinator = factory.Services.GetRequiredService<RuntimeCoordinator>();
        Assert.Equal(0, input.Runs);
        var applied = await coordinator.ApplyDraftAsync(saved.Version, "operator", null, Token);
        Assert.True(applied.Committed && applied.Activated);
        await OutputPipelineTests.UntilAsync(() => coordinator.Runtime.Deliveries.GetStatus().CommittedRecords > 0);
        var drafts = factory.Services.GetRequiredService<RouteDraftStore>();
        var edited = drafts.Write(saved.Route.Id, new(saved.Version, saved.Route.Key, saved.Route.Name, false, saved.Route.Graph), "editor", null);
        Assert.Equal(1, input.Runs);
        Assert.Equal(1, coordinator.Runtime.ActiveRevision);
        var rejected = await coordinator.ApplyDraftAsync(edited.Version, "operator", null, Token);
        Assert.False(rejected.Committed);
        Assert.Equal("runtime.targetChangeBlocked", rejected.Code);
        Assert.Equal(1, input.Runs); // First target check rejects without interrupting old collection.
        Assert.Equal(1, coordinator.Runtime.ActiveRevision);
    }

    [Fact]
    public async Task SecondTargetCheckSeesRecordsProducedWhileOldInputIsQuiescing()
    {
        using var data = TemporaryDirectory.Create();
        using var input = new ControlledInput { ProduceOnlyOnFirstStop = true, StopWorkDelay = TimeSpan.FromMilliseconds(200) };
        await using var factory = Create(data.Path, input);
        var saved = Seed(factory);
        var coordinator = factory.Services.GetRequiredService<RuntimeCoordinator>();
        Assert.True((await coordinator.ApplyDraftAsync(saved.Version, "operator", null, Token)).Activated);
        await OutputPipelineTests.UntilAsync(() => input.Runs == 1);
        Assert.Equal(0, coordinator.Runtime.Deliveries.GetStatus().CommittedRecords);
        var drafts = factory.Services.GetRequiredService<RouteDraftStore>();
        var edited = drafts.Write(saved.Route.Id, new(saved.Version, saved.Route.Key, saved.Route.Name, false, saved.Route.Graph), "editor", null);
        var rejected = await coordinator.ApplyDraftAsync(edited.Version, "operator", null, Token);
        Assert.Equal("runtime.targetChangeBlocked", rejected.Code);
        Assert.False(rejected.Committed);
        await OutputPipelineTests.UntilAsync(() => input.Runs == 2);
        Assert.Equal(1, coordinator.Runtime.Deliveries.GetStatus().CommittedRecords);
        var versions = factory.Services.GetRequiredService<RuntimeVersionStore>();
        Assert.Equal(1, versions.ReadPointers().EffectiveRevision);
        Assert.Equal(1, versions.ReadPointers().RunningRevision);
        Assert.Null(versions.ReadPointers().PendingRevision);
        Assert.Equal(RuntimeSnapshotStates.NotEffective, versions.Load(2)!.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedCommitOrInputTimeoutRestoresOriginalVersion(bool timeout)
    {
        using var data = TemporaryDirectory.Create();
        using var input = new ControlledInput { BlockFirstStop = timeout };
        await using var factory = Create(data.Path, input);
        var saved = Seed(factory);
        var coordinator = factory.Services.GetRequiredService<RuntimeCoordinator>();
        var versions = factory.Services.GetRequiredService<RuntimeVersionStore>();
        var database = factory.Services.GetRequiredService<ConfigDatabase>();
        Assert.True((await coordinator.ApplyDraftAsync(saved.Version, "operator", null, Token)).Activated);
        await OutputPipelineTests.UntilAsync(() => input.Runs == 1);
        if (!timeout)
        { database.Client.Ado.ExecuteCommand("CREATE TRIGGER fail_lifecycle_commit BEFORE UPDATE OF effective_revision ON system_state WHEN NEW.effective_revision = 2 BEGIN SELECT RAISE(ABORT, 'fixture-commit-fault'); END"); }
        try
        {
            var result = await coordinator.ApplyDraftAsync(saved.Version, "operator", null, Token);
            Assert.False(result.Committed);
            Assert.Equal(timeout ? "runtime.quiescenceFailed" : "runtime.commitFailed", result.Code);
            Assert.Equal(timeout, result.RecoveryPending);
            Assert.Equal(1, versions.ReadPointers().EffectiveRevision);
            Assert.Equal(1, versions.ReadPointers().RunningRevision);
            Assert.Null(versions.ReadPointers().PendingRevision);
            Assert.Equal(RuntimeSnapshotStates.NotEffective, versions.Load(2)!.State);
            input.ReleaseStop.TrySetResult();
            await OutputPipelineTests.UntilAsync(() => input.Runs == 2);
            Assert.Equal(1, coordinator.Runtime.ActiveRevision);
        }
        finally
        {
            input.ReleaseStop.TrySetResult();
            if (!timeout)
            { database.Client.Ado.ExecuteCommand("DROP TRIGGER fail_lifecycle_commit"); }
        }
    }

    private static FixedDataDirectoryApplicationFactory Create(string path, ControlledInput input) => new(path, configureServices: services =>
    {
        services.RemoveAll<IInputUnitFactory>();
        services.AddSingleton<IInputUnitFactory>(input);
        services.RemoveAll<IOutboundConnector>();
        services.AddSingleton<IOutboundConnector, DeniedConnector>();
        if (input.BlockFirstStop)
        {
            // 只有专门验证停止超时的场景缩短期限；正常排空使用产品默认值。
            services.RemoveAll<RuntimeOptions>();
            services.AddSingleton(new RuntimeOptions { UnitStopTimeout = TimeSpan.FromMilliseconds(100) });
        }
    });

    private static RouteDraftResult Seed(FixedDataDirectoryApplicationFactory factory)
    {
        var device = factory.Services.GetRequiredService<DeviceDraftStore>().Write(null, new("0", "meter", "Meter", DeviceKind.Simulator), "test", null);
        var input = factory.Services.GetRequiredService<InputDraftStore>().Write(device.Device.Id, null,
            new(device.Version, "sample", "Sample", Simulator: new(SampleJson: "{\"value\":42}")), "test", null);
        var connection = factory.Services.GetRequiredService<ConnectionDraftStore>().Write(null,
            new(input.Version, "http", "HTTP", ConnectionKind.Http, Http: new("http://localhost/")), "test", null);
        var graph = new RouteGraph(1,
            [new("in", "input.simulator", 1, "Input", JsonSerializer.SerializeToElement(new { sourceIds = new[] { input.Input.Id } })),
             new("out", "output.http", 1, "Output", JsonSerializer.SerializeToElement(new { connectionId = connection.Connection.Id }))],
            [new("in", "out")], new([]));
        return factory.Services.GetRequiredService<RouteDraftStore>().Write(null, new(connection.Version, "route", "Route", true, graph), "test", null);
    }

    private sealed class DeniedConnector : IOutboundConnector
    {
        public ValueTask<Stream> ConnectAsync(string host, int port, CancellationToken cancellationToken) => throw new OutboundAccessDeniedException();
    }

    private sealed class ControlledInput : IInputUnitFactory, IDisposable
    {
        private readonly SimulatorInputFactory _source = new();
        public string Kind => _source.Kind;
        public bool BlockPreparation { get; init; }
        public bool RejectPreparation { get; set; }
        public bool BlockFirstStop { get; init; }
        public bool ProduceOnlyOnFirstStop { get; init; }
        public TimeSpan StopWorkDelay { get; init; }
        public TaskCompletionSource Preparing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim ReleasePreparation { get; } = new();
        public TaskCompletionSource ReleaseStop { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Runs;
        public IRuntimeUnit Build(RuntimeUnitPlan plan, InputUnitContext context)
        {
            if (RejectPreparation)
            { throw new InvalidOperationException("fixture-sensitive-build-detail"); }
            if (BlockPreparation)
            {
                Preparing.TrySetResult();
                if (!ReleasePreparation.Wait(Deadline))
                { throw new InvalidOperationException("fixture-preparation-timeout"); }
            }
            return new Unit(this, _source.Build(plan, context), context);
        }
        public void Dispose() { ReleasePreparation.Set(); ReleaseStop.TrySetResult(); ReleasePreparation.Dispose(); }
        private sealed class Unit(ControlledInput owner, IRuntimeUnit inner, InputUnitContext context) : IRuntimeUnit
        {
            public RuntimeUnitId Id => inner.Id;
            public async Task RunAsync(CancellationToken stoppingToken)
            {
                var generation = Interlocked.Increment(ref owner.Runs);
                try
                {
                    if (owner.ProduceOnlyOnFirstStop)
                    { await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken); }
                    else
                    { await inner.RunAsync(stoppingToken); }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
                if (generation == 1 && owner.ProduceOnlyOnFirstStop)
                {
                    // 有界的收尾工作，不要求真实 SQLite/线程池在测试专用的 100 ms 内完成。
                    await Task.Delay(owner.StopWorkDelay, CancellationToken.None);
                    await context.Messages.ProduceAsync(1024, _ =>
                    {
                        var now = context.Time.GetUtcNow();
                        return ValueTask.FromResult<EdgeMessage?>(new(Guid.NewGuid(), context.InputKey, context.DeviceKey, now,
                            TimeSource.GatewayUnsynced, now, new Dictionary<string, FieldValue> { ["value"] = FieldValue.FromInt64(42) }));
                    }, CancellationToken.None);
                }
                if (generation == 1 && owner.BlockFirstStop)
                { await owner.ReleaseStop.Task; }
            }
            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }
}
