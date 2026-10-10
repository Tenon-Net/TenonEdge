using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Abstractions.Plans;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.Runtime.Diagnostics;
using TenonEdge.Runtime.Lifecycle;
using static TenonEdge.IntegrationTests.Runtime.OutputPipelineTests;

namespace TenonEdge.IntegrationTests.Runtime;

public sealed class RuntimeQuiescenceTests
{
    [Fact]
    public async Task OnlyLatestSuccessfulQuiescenceFromThisRuntimeIsCurrent()
    {
        using var data = TemporaryDirectory.Create();
        using var otherData = TemporaryDirectory.Create();
        await using var runtime = RuntimeTesting.CreateRuntime([], data.Path);
        await using var other = RuntimeTesting.CreateRuntime([], otherData.Path);
        await using var plan = runtime.Prepare(Plan());
        runtime.StartCore();
        await runtime.ActivateAsync(plan);
        var first = await runtime.QuiesceInputsAsync(TestContext.Current.CancellationToken);
        Assert.True(runtime.IsCurrentQuiescence(first));
        Assert.False(other.IsCurrentQuiescence(first));
        Assert.False(runtime.IsCurrentQuiescence(first with { }));
        Assert.False(runtime.IsCurrentQuiescence(new(1, true, [])));
        var latest = await runtime.QuiesceInputsAsync(TestContext.Current.CancellationToken);
        Assert.False(runtime.IsCurrentQuiescence(first));
        Assert.True(runtime.IsCurrentQuiescence(latest));
        await runtime.StopAsync(TestContext.Current.CancellationToken);
        Assert.False(runtime.IsCurrentQuiescence(latest));
        Assert.True(latest.Succeeded); // Historical result remains true, but cannot authorize a later commit.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoveryRevokesSuccessfulQuiescenceEvenWhenRebuildFails(bool rejectBuild)
    {
        using var data = TemporaryDirectory.Create();
        var source = new OneShotInputFactory();
        var factory = new RepairableInputFactory(source);
        await using var runtime = RuntimeTesting.CreateRuntime([factory], data.Path);
        await using var plan = runtime.Prepare(Plan(Input()));
        runtime.StartCore();
        await runtime.ActivateAsync(plan);
        await source.Written.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var result = await runtime.QuiesceInputsAsync(TestContext.Current.CancellationToken);
        Assert.True(runtime.IsCurrentQuiescence(result));
        factory.Reject = rejectBuild;
        var recovery = await runtime.RestoreInputsAsync();
        Assert.Equal(!rejectBuild, recovery.Succeeded);
        Assert.False(runtime.IsCurrentQuiescence(result));
        Assert.Equal(1, runtime.ActiveRevision);
    }

    [Fact]
    public async Task RecoveryBuildFailureKeepsRevisionAndCanBeRetriedAfterRepair()
    {
        using var data = TemporaryDirectory.Create();
        var source = new OneShotInputFactory();
        var factory = new RepairableInputFactory(source);
        await using var runtime = RuntimeTesting.CreateRuntime([factory], data.Path);
        var idle = await runtime.RestoreInputsAsync();
        Assert.True(idle.Succeeded);
        Assert.False(idle.Restarted);
        await using var plan = runtime.Prepare(Plan(Input()));
        runtime.StartCore();
        await runtime.ActivateAsync(plan);
        await source.Written.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.True((await runtime.QuiesceInputsAsync(TestContext.Current.CancellationToken)).Succeeded);
        factory.Reject = true;
        var failed = await runtime.RestoreInputsAsync();
        Assert.False(failed.Succeeded);
        Assert.Equal(Input().Id, Assert.Single(failed.Failures).Id);
        Assert.Equal(1, runtime.ActiveRevision);
        Assert.Equal(RuntimeUnitState.BuildFailed, Assert.Single(runtime.GetStatus().Units, unit => unit.Id == Input().Id).State);
        Assert.Equal(1, source.Started);
        factory.Reject = false;
        Assert.True((await runtime.RestoreInputsAsync()).Succeeded);
        await UntilAsync(() => source.Started == 2);
        Assert.Equal(RuntimeUnitState.Running, Assert.Single(runtime.GetStatus().Units, unit => unit.Id == Input().Id).State);
    }

    [Fact]
    public async Task RecoveryRebuildsInputChannelsButKeepsIndependentSenderAndOldRevision()
    {
        using var data = TemporaryDirectory.Create();
        var input = new OneShotInputFactory();
        var output = new OutputFactory();
        await using var runtime = RuntimeTesting.CreateRuntime([input], data.Path, outputs: [output]);
        await using var plan = runtime.Prepare(Plan(Input(), Output(1, false), Output(2, true)));
        runtime.StartCore();
        await runtime.ActivateAsync(plan);
        await UntilAsync(() => runtime.Deliveries.GetStatus().CommittedRecords == 2 && output.Created.Count == 2);
        for (var round = 1; round <= 2; round++)
        {
            Assert.True((await runtime.QuiesceInputsAsync(TestContext.Current.CancellationToken)).Succeeded);
            var restored = await runtime.RestoreInputsAsync();
            Assert.True(restored.Succeeded);
            Assert.True(restored.Restarted);
            Assert.Equal(1, restored.Revision);
            await UntilAsync(() => runtime.Deliveries.GetStatus().CommittedRecords == 2 * (round + 1) && output.Created.Count == round + 2);
            Assert.Equal(round + 1, input.Started);
            Assert.Equal(1, output.Created.Count(id => id == 1));
            Assert.DoesNotContain(1L, output.Disposed);
            Assert.Equal(round + 1, output.Created.Count(id => id == 2));
            Assert.All(runtime.Deliveries.ReadPending(1, 0, 10), record => Assert.Equal(1, record.Revision));
            Assert.False((await runtime.RestoreInputsAsync()).Restarted);
            Assert.Equal(1, runtime.ActiveRevision);
            Assert.Equal(5, runtime.GetStatus().Units.Count);
        }
    }

    [Fact]
    public async Task RecoveryCannotBuildNewInputUntilTimedOutOldInputExits()
    {
        using var data = TemporaryDirectory.Create();
        var input = new GatedInputFactory();
        await using var runtime = RuntimeTesting.CreateRuntime([input], data.Path,
            options: new RuntimeOptions { UnitStopTimeout = TimeSpan.FromMilliseconds(50) });
        await using var plan = runtime.Prepare(Plan(Input()));
        runtime.StartCore();
        await runtime.ActivateAsync(plan);
        await input.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        try
        {
            Assert.False((await runtime.QuiesceInputsAsync(TestContext.Current.CancellationToken)).Succeeded);
            var blocked = await runtime.RestoreInputsAsync();
            Assert.False(blocked.Succeeded);
            Assert.False(blocked.Restarted);
            Assert.NotNull(blocked.BlockedBy);
            Assert.Equal(1, input.Builds);
            input.Release.TrySetResult();
            // 不再调用恢复入口：旧实例退出后应由运行时拥有的恢复任务重新构建。
            await UntilAsync(() => input.Runs == 2);
            Assert.Equal(2, input.Builds);
        }
        finally { input.Release.TrySetResult(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CoupledSenderRecoveryDeletesPreviouslyAcknowledgedRecordWithoutResendingIt(bool rebuildFails)
    {
        using var data = TemporaryDirectory.Create();
        var output = new OutputFactory { SendStatus = OutputSendStatus.Ack };
        await using var runtime = RuntimeTesting.CreateRuntime([new OneShotInputFactory()], data.Path, outputs: [output]);
        _ = runtime.Deliveries.GetPersistentCounters();
        runtime.StartCore();
        using var database = new SqliteConnection($"Data Source={Path.Combine(data.Path, "queue.db")};Pooling=False");
        database.Open();
        using var command = database.CreateCommand();
        command.CommandText = "CREATE TRIGGER fail_recovery_delete BEFORE DELETE ON delivery BEGIN SELECT RAISE(ABORT, 'injected'); END";
        command.ExecuteNonQuery();
        await using var plan = runtime.Prepare(Plan(Input(), Output(1, true)));
        await runtime.ActivateAsync(plan);
        await UntilAsync(() => output.Sends == 1 && runtime.GetStatus().Units.Single(unit => unit.Id.Kind == "output.test").State == RuntimeUnitState.Backoff);
        Assert.True((await runtime.QuiesceInputsAsync(TestContext.Current.CancellationToken)).Succeeded);
        Assert.Single(runtime.Deliveries.ReadPending(1, 0, 10));
        command.CommandText = "DROP TRIGGER fail_recovery_delete";
        command.ExecuteNonQuery();
        if (rebuildFails)
        {
            output.RejectBuild = true;
            Assert.False((await runtime.RestoreInputsAsync()).Succeeded);
            Assert.Equal(1, output.Sends);
            output.RejectBuild = false;
        }
        Assert.True((await runtime.RestoreInputsAsync()).Succeeded);
        await UntilAsync(() => output.Sends >= 2 && runtime.Deliveries.ReadPending(1, 0, 10).Count == 0);
        Assert.Equal(2, output.Sends); // One ACK in each input generation, never a third send for the old record.
    }

    [Fact]
    public async Task DrainsOldRevisionAndStopsOnlyInputCoupledSenders()
    {
        using var data = TemporaryDirectory.Create();
        var input = new OneShotInputFactory(10);
        var output = new OutputFactory();
        await using var runtime = RuntimeTesting.CreateRuntime([input], data.Path, outputs: [output]);
        await using var plan = runtime.Prepare(Plan(Input(), Output(1, false), Output(2, true)));
        Assert.Empty(plan.Failures);
        runtime.StartCore();
        await runtime.ActivateAsync(plan);
        await input.Written.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await UntilAsync(() => output.Created.Count == 2);
        var result = await runtime.QuiesceInputsAsync(TestContext.Current.CancellationToken);
        Assert.True(result.Succeeded);
        Assert.Equal(1, result.Revision);
        Assert.Equal(1, runtime.ActiveRevision);
        Assert.Equal(20, runtime.Deliveries.GetStatus().CommittedRecords);
        Assert.All(runtime.Deliveries.ReadPending(1, 0, 20), record => Assert.Equal(1, record.Revision));
        Assert.All(runtime.Deliveries.ReadPending(2, 0, 20), record => Assert.Equal(1, record.Revision));
        Assert.Equal(0, runtime.InFlight.Status.ReservedBytes);
        Assert.Contains(2L, output.Disposed);
        Assert.DoesNotContain(1L, output.Disposed);
        Assert.All(runtime.GetStatus().Units.Where(unit => unit.Id.Kind is "input.test" or "delivery.dispatcher"),
            unit => Assert.Equal(RuntimeUnitState.Stopped, unit.State));
        Assert.NotEqual(RuntimeUnitState.Stopped, Assert.Single(runtime.GetStatus().Units, unit => unit.Id.Kind == "delivery.writer").State);
        // A real queue transaction still works: quiescence must not CompleteAdding on the writer.
        var reconciled = await runtime.Deliveries.ReconcileAsync(new Dictionary<long, string> { [1] = "fingerprint", [2] = "fingerprint" }, true,
            TestContext.Current.CancellationToken);
        Assert.True(reconciled.Committed);
        Assert.True((await runtime.QuiesceInputsAsync(TestContext.Current.CancellationToken)).Succeeded);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DrainTimeoutRetainsWorkAndBudgetAndCanBeRetried(bool diagnosticFault)
    {
        using var data = TemporaryDirectory.Create();
        using var release = new ManualResetEventSlim();
        var formatting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var output = new OutputFactory { BeforeFormat = () => { formatting.TrySetResult(); Assert.True(release.Wait(TimeSpan.FromSeconds(30))); } };
        var events = new RecordingEventSink(recorded =>
        {
            if (diagnosticFault && recorded.Type == "runtime.quiesceDrainTimedOut")
            { throw new InvalidOperationException("injected-diagnostic-fault"); }
        });
        await using var runtime = RuntimeTesting.CreateRuntime([new OneShotInputFactory()], data.Path, events, outputs: [output],
            options: new RuntimeOptions { UnitStopTimeout = TimeSpan.FromMilliseconds(100) });
        await using var plan = runtime.Prepare(Plan(Input(), Output(1, false)));
        runtime.StartCore();
        await runtime.ActivateAsync(plan);
        try
        {
            await formatting.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            var budget = runtime.InFlight.Status.ReservedBytes;
            Assert.True(budget > 0);
            var result = await runtime.QuiesceInputsAsync(TestContext.Current.CancellationToken);
            Assert.False(result.Succeeded);
            Assert.False(result.Drained);
            Assert.False(runtime.IsCurrentQuiescence(result));
            Assert.Equal(diagnosticFault ? 1 : 0, events.OfType("runtime.quiesceFailed").Count);
            Assert.Equal(budget, runtime.InFlight.Status.ReservedBytes);
            Assert.Equal(RuntimeUnitState.Running, Assert.Single(runtime.GetStatus().Units, unit => unit.Id.Kind == "delivery.dispatcher").State);
        }
        finally { release.Set(); }
        await UntilAsync(() => runtime.Deliveries.GetStatus().CommittedRecords == 1);
        Assert.True((await runtime.QuiesceInputsAsync(TestContext.Current.CancellationToken)).Succeeded);
        Assert.Equal(0, runtime.InFlight.Status.ReservedBytes);
    }

    [Fact]
    public async Task PreCancelledAndIdleQuiescenceDoNotPreventInitialActivation()
    {
        using var data = TemporaryDirectory.Create();
        var input = new OneShotInputFactory();
        await using var runtime = RuntimeTesting.CreateRuntime([input], data.Path);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runtime.QuiesceInputsAsync(cancelled.Token));
        var idle = await runtime.QuiesceInputsAsync(TestContext.Current.CancellationToken);
        Assert.True(idle.Succeeded);
        Assert.Null(idle.Revision);
        Assert.False(runtime.IsCurrentQuiescence(idle));
        await using var plan = runtime.Prepare(Plan(Input()));
        runtime.StartCore();
        await runtime.ActivateAsync(plan);
        await input.Written.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(1, input.Started);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runtime.QuiesceInputsAsync(cancelled.Token));
        Assert.Equal(RuntimeUnitState.Running, runtime.GetStatus().Units.Single(unit => unit.Id == Input().Id).State);
        var quiet = await runtime.QuiesceInputsAsync(TestContext.Current.CancellationToken);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runtime.QuiesceInputsAsync(cancelled.Token));
        Assert.True(runtime.IsCurrentQuiescence(quiet));
    }

    [Fact]
    public async Task CancellingWaitDoesNotCancelOwnedCleanupAndStopWaitsForIt()
    {
        using var data = TemporaryDirectory.Create();
        var input = new GatedInputFactory();
        await using var runtime = RuntimeTesting.CreateRuntime([input], data.Path,
            options: new RuntimeOptions { UnitStopTimeout = TimeSpan.FromSeconds(10) });
        await using var plan = runtime.Prepare(Plan(Input()));
        runtime.StartCore();
        await runtime.ActivateAsync(plan);
        await input.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        var quiescing = runtime.QuiesceInputsAsync(cancellation.Token);
        try
        {
            await input.Stopping.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => quiescing);
            await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.QuiesceInputsAsync(CancellationToken.None));
            await using var duplicate = runtime.Prepare(Plan(Input()));
            await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.ActivateAsync(duplicate));
            var stop = runtime.StopAsync(TestContext.Current.CancellationToken);
            Assert.False(stop.IsCompleted);
            input.Release.TrySetResult();
            await stop.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.All(runtime.GetStatus().Units, unit => Assert.Equal(RuntimeUnitState.Stopped, unit.State));
            Assert.Equal(1, input.Runs);
        }
        finally { input.Release.TrySetResult(); }
    }

    [Fact]
    public async Task InputTimeoutRetainsDispatcherAndDoesNotStartDuplicate()
    {
        using var data = TemporaryDirectory.Create();
        var input = new GatedInputFactory();
        await using var runtime = RuntimeTesting.CreateRuntime([input], data.Path,
            options: new RuntimeOptions { UnitStopTimeout = TimeSpan.FromMilliseconds(100) });
        await using var plan = runtime.Prepare(Plan(Input()));
        runtime.StartCore();
        await runtime.ActivateAsync(plan);
        await input.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        try
        {
            var result = await runtime.QuiesceInputsAsync(TestContext.Current.CancellationToken);
            Assert.False(result.Succeeded);
            Assert.Equal(Input().Id, Assert.Single(result.TimedOutUnits));
            Assert.False(runtime.IsCurrentQuiescence(result));
            Assert.Equal(RuntimeUnitState.StopTimedOut, Assert.Single(runtime.GetStatus().Units, unit => unit.Id.Kind == "input.test").State);
            Assert.Equal(RuntimeUnitState.Running, Assert.Single(runtime.GetStatus().Units, unit => unit.Id.Kind == "delivery.dispatcher").State);
            Assert.False((await runtime.QuiesceInputsAsync(TestContext.Current.CancellationToken)).Succeeded);
            Assert.Equal(1, input.Runs);
        }
        finally { input.Release.TrySetResult(); }
        await UntilAsync(() => runtime.GetStatus().Units.Single(unit => unit.Id.Kind == "input.test").State == RuntimeUnitState.Stopped);
        Assert.True((await runtime.QuiesceInputsAsync(TestContext.Current.CancellationToken)).Succeeded);
    }

    private static RuntimeUnitPlan Output(long connection, bool coupled) => new(RuntimeUnitId.Create("output.test", $"out-{connection}"),
        JsonSerializer.SerializeToElement(new { connection, coupled }));

    private sealed class OutputFactory : IOutputAdapterFactory
    {
        public string Kind => "output.test";
        public Action? BeforeFormat { get; init; }
        public OutputSendStatus SendStatus { get; init; } = OutputSendStatus.SuspendTarget;
        public int Sends;
        public ConcurrentBag<long> Disposed { get; } = [];
        public ConcurrentBag<long> Created { get; } = [];
        public bool RejectBuild { get; set; }
        public IOutputTarget Build(RuntimeUnitPlan plan) => RejectBuild ? throw new InvalidOperationException("injected-build-failure") : new Target(this, plan);
        public RuntimeUnitId? RecoveryGroup(RuntimeUnitPlan plan) => plan.Configuration.GetProperty("coupled").GetBoolean() ? Input().Id : null;
        private sealed class Target(OutputFactory factory, RuntimeUnitPlan plan) : IOutputTarget
        {
            public RuntimeUnitId Id => plan.Id;
            public long ConnectionId => plan.Configuration.GetProperty("connection").GetInt64();
            public bool QuiesceWithInputs => plan.Configuration.GetProperty("coupled").GetBoolean();
            public IReadOnlyList<string> Inputs => ["meter/sim"];
            public string? ClientId => null;
            public string Recipient => "test";
            public string CredentialVersion => "test";
            public string CurrentFingerprint => "fingerprint";
            public OutputMessage CreateMessage(EdgeMessage message, Guid deliveryId)
            {
                factory.BeforeFormat?.Invoke();
                return new(StandardEnvelope.Serialize(message, deliveryId), "test", 1, CurrentFingerprint, "test");
            }
            public IOutputAdapter CreateAdapter()
            {
                factory.Created.Add(ConnectionId);
                return new Adapter(factory, ConnectionId);
            }
        }
        private sealed class Adapter(OutputFactory factory, long connection) : IOutputAdapter
        {
            public Task<OutputSendResult> SendAsync(OutputMessage message, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref factory.Sends);
                return Task.FromResult(new OutputSendResult(factory.SendStatus, "test-paused"));
            }
            public ValueTask DisposeAsync() { factory.Disposed.Add(connection); return ValueTask.CompletedTask; }
        }
    }

    private sealed class RepairableInputFactory(OneShotInputFactory source) : IInputUnitFactory
    {
        public string Kind => source.Kind;
        public bool Reject { get; set; }
        public IRuntimeUnit Build(RuntimeUnitPlan plan, InputUnitContext context) =>
            Reject ? throw new InvalidOperationException("injected-build-failure") : source.Build(plan, context);
    }

    private sealed class GatedInputFactory : IInputUnitFactory
    {
        public string Kind => "input.test";
        public int Runs;
        public int Builds;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Stopping { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IRuntimeUnit Build(RuntimeUnitPlan plan, InputUnitContext context)
        { Interlocked.Increment(ref Builds); return new Unit(this, plan.Id); }
        private sealed class Unit(GatedInputFactory factory, RuntimeUnitId id) : IRuntimeUnit
        {
            public RuntimeUnitId Id => id;
            public async Task RunAsync(CancellationToken stoppingToken)
            {
                var generation = Interlocked.Increment(ref factory.Runs);
                factory.Started.TrySetResult();
                using var registration = stoppingToken.Register(() => factory.Stopping.TrySetResult());
                if (generation == 1)
                { await factory.Release.Task; }
                else
                { await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken); }
            }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
