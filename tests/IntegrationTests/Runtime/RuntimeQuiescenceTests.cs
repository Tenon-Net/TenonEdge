using System.Collections.Concurrent;
using System.Text.Json;
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
        await using var plan = runtime.Prepare(Plan(Input()));
        runtime.StartCore();
        await runtime.ActivateAsync(plan);
        await input.Written.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(1, input.Started);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runtime.QuiesceInputsAsync(cancelled.Token));
        Assert.Equal(RuntimeUnitState.Running, runtime.GetStatus().Units.Single(unit => unit.Id == Input().Id).State);
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
        public ConcurrentBag<long> Disposed { get; } = [];
        public ConcurrentBag<long> Created { get; } = [];
        public IOutputTarget Build(RuntimeUnitPlan plan) => new Target(this, plan);
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
            public Task<OutputSendResult> SendAsync(OutputMessage message, CancellationToken cancellationToken) =>
                Task.FromResult(new OutputSendResult(OutputSendStatus.SuspendTarget, "test-paused"));
            public ValueTask DisposeAsync() { factory.Disposed.Add(connection); return ValueTask.CompletedTask; }
        }
    }

    private sealed class GatedInputFactory : IInputUnitFactory
    {
        public string Kind => "input.test";
        public int Runs;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Stopping { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IRuntimeUnit Build(RuntimeUnitPlan plan, InputUnitContext context) => new Unit(this, plan.Id);
        private sealed class Unit(GatedInputFactory factory, RuntimeUnitId id) : IRuntimeUnit
        {
            public RuntimeUnitId Id => id;
            public async Task RunAsync(CancellationToken stoppingToken)
            {
                Interlocked.Increment(ref factory.Runs);
                factory.Started.TrySetResult();
                using var registration = stoppingToken.Register(() => factory.Stopping.TrySetResult());
                await factory.Release.Task;
            }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
