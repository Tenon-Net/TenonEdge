using System.Collections.Concurrent;
using System.Text.Json;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Abstractions.Plans;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.Runtime.Lifecycle;
using static TenonEdge.IntegrationTests.Runtime.OutputPipelineTests;

namespace TenonEdge.IntegrationTests.Runtime;

public sealed class RuntimeTransitionTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task ExplicitRetryCanReactivateSameRevisionButOrdinaryApplicationCannotReuseIt()
    {
        using var directory = TemporaryDirectory.Create();
        var input = new OneShotInputFactory();
        var output = new Factory();
        await using var runtime = RuntimeTesting.CreateRuntime([input], directory.Path, outputs: [output]);
        runtime.StartCore();
        var definition = Revision(1, Output("same", "same"));
        await using var first = runtime.Prepare(definition);
        await runtime.ActivateAsync(first);
        await UntilAsync(() => runtime.Deliveries.GetStatus().CommittedRecords == 1 && output.Created.Count == 1);
        var quiet = await runtime.QuiesceInputsAsync(Token);
        await using var changed = runtime.Prepare(Revision(1, Output("same", "changed")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.RetryEffectiveAsync(changed, quiet));
        Assert.True(runtime.IsCurrentQuiescence(quiet));
        await using var retry = runtime.Prepare(definition);
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.ActivateCommittedAsync(retry, quiet));
        Assert.True(runtime.IsCurrentQuiescence(quiet));
        await runtime.RetryEffectiveAsync(retry, quiet);
        await UntilAsync(() => runtime.Deliveries.GetStatus().CommittedRecords == 2);
        Assert.Equal(1, runtime.ActiveRevision);
        Assert.Equal(2, input.Started);
        Assert.Single(output.Created); // Unchanged independent sender was not duplicated.
        Assert.Equal(new long[] { 1, 1 }, runtime.Deliveries.ReadPending(1, 0, 10).Select(record => record.Revision));
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.RetryEffectiveAsync(retry, quiet));
        Assert.Equal(2, input.Started);
    }

    [Fact]
    public async Task PreparedRevisionDoesNotRunAndUnchangedSenderSurvivesAliasChangeAndRecovery()
    {
        using var directory = TemporaryDirectory.Create();
        var input = new OneShotInputFactory();
        var output = new Factory();
        await using var runtime = RuntimeTesting.CreateRuntime([input], directory.Path, outputs: [output]);
        runtime.StartCore();
        await using var first = runtime.Prepare(Revision(1, Output("old", "same")));
        await runtime.ActivateAsync(first);
        await UntilAsync(() => runtime.Deliveries.GetStatus().CommittedRecords == 1 && output.Created.Count == 1);
        await using var next = runtime.Prepare(Revision(2, Output("renamed", "same")));
        Assert.Equal(1, input.Started);
        Assert.Single(output.Created);
        var quiet = await runtime.QuiesceInputsAsync(Token);
        Assert.True(quiet.Succeeded);
        await runtime.ActivateCommittedAsync(next, quiet);
        await UntilAsync(() => runtime.Deliveries.GetStatus().CommittedRecords == 2);
        Assert.Equal(2, runtime.ActiveRevision);
        Assert.False(runtime.IsCurrentQuiescence(quiet));
        Assert.Single(output.Created);
        Assert.Empty(output.Disposed);
        Assert.Equal(new long[] { 1, 2 }, runtime.Deliveries.ReadPending(1, 0, 10).Select(record => record.Revision));
        Assert.True((await runtime.QuiesceInputsAsync(Token)).Succeeded);
        Assert.True((await runtime.RestoreInputsAsync()).Succeeded);
        await UntilAsync(() => runtime.Deliveries.GetStatus().CommittedRecords == 3);
        Assert.Single(output.Created);
        Assert.Equal(2, runtime.Deliveries.ReadPending(1, 0, 10)[2].Revision);
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.ActivateCommittedAsync(next, quiet));
        Assert.Equal(2, runtime.ActiveRevision);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangedSenderWaitsForLateAckAndSharedInputsWaitForFullConnectionExit(bool shared)
    {
        using var directory = TemporaryDirectory.Create();
        var input = new OneShotInputFactory();
        var output = new Factory { BlockOld = true, Result = OutputSendStatus.Ack };
        await using var runtime = RuntimeTesting.CreateRuntime([input], directory.Path, outputs: [output],
            options: new RuntimeOptions { UnitStopTimeout = TimeSpan.FromMilliseconds(50) });
        runtime.StartCore();
        await using var first = runtime.Prepare(Revision(1, Output("old", "old", metadata: shared)));
        await runtime.ActivateAsync(first);
        try
        {
            await output.Sending.Task.WaitAsync(Deadline, Token);
            await using var next = runtime.Prepare(Revision(2, Output("renamed", "new", coupled: shared, metadata: shared)));
            await runtime.ActivateCommittedAsync(next, await runtime.QuiesceInputsAsync(Token));
            Assert.Equal(2, runtime.ActiveRevision);
            Assert.Single(output.Created);
            Assert.Contains(runtime.GetStatus().Units, unit => unit.Id.Key == "old" && unit.State == RuntimeUnitState.StopTimedOut);
            if (!shared)
            { await UntilAsync(() => input.Started == 2); }
            else
            {
                Assert.Equal(1, input.Started);
                var incomplete = await runtime.QuiesceInputsAsync(Token);
                Assert.False(incomplete.Succeeded);
                await using var forbidden = runtime.Prepare(Revision(3, Output("third", "forbidden", coupled: true, metadata: true)));
                await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.ActivateCommittedAsync(forbidden, incomplete));
                Assert.Equal(2, runtime.ActiveRevision);
                Assert.False((await runtime.RestoreInputsAsync()).Succeeded);
            }
            output.ReleaseSend.TrySetResult();
            await output.Disposing.Task.WaitAsync(Deadline, Token);
            Assert.Single(output.Created);
            output.ReleaseDispose.TrySetResult();
            await UntilAsync(() => input.Started == 2 && runtime.Deliveries.GetStatus().DeletedRecords == 2);
            Assert.Equal<string>(["old", "new"], output.Created);
            Assert.Equal(2, output.Sends); // One send for each revision; late old ACK is not sent again.
            Assert.Empty(runtime.Deliveries.ReadPending(1, 0, 10));
        }
        finally
        {
            output.ReleaseSend.TrySetResult();
            output.ReleaseDispose.TrySetResult();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NewerRevisionOrShutdownRevokesWaitingSender(bool shutdown)
    {
        using var directory = TemporaryDirectory.Create();
        var input = new OneShotInputFactory();
        var output = new Factory { BlockOld = true, Result = OutputSendStatus.Ack };
        await using var runtime = RuntimeTesting.CreateRuntime([input], directory.Path, outputs: [output],
            options: new RuntimeOptions { UnitStopTimeout = TimeSpan.FromMilliseconds(50) });
        runtime.StartCore();
        await using var first = runtime.Prepare(Revision(1, Output("old", "old")));
        await runtime.ActivateAsync(first);
        try
        {
            await output.Sending.Task.WaitAsync(Deadline, Token);
            await using var second = runtime.Prepare(Revision(2, Output("second", "obsolete")));
            await runtime.ActivateCommittedAsync(second, await runtime.QuiesceInputsAsync(Token));
            await UntilAsync(() => runtime.Deliveries.GetStatus().CommittedRecords == 2);
            if (shutdown)
            { await runtime.StopAsync(Token); }
            else
            {
                await using var third = runtime.Prepare(Revision(3, Output("third", "latest")));
                await runtime.ActivateCommittedAsync(third, await runtime.QuiesceInputsAsync(Token));
                await UntilAsync(() => runtime.Deliveries.GetStatus().CommittedRecords == 3);
            }
            output.ReleaseSend.TrySetResult();
            output.ReleaseDispose.TrySetResult();
            if (shutdown)
            {
                await UntilAsync(() => runtime.GetStatus().Units.All(unit => unit.State == RuntimeUnitState.Stopped));
                Assert.Single(output.Created);
            }
            else
            {
                await UntilAsync(() => runtime.Deliveries.GetStatus().DeletedRecords == 3);
                Assert.Equal<string>(["old", "latest"], output.Created);
                Assert.Equal(3, runtime.ActiveRevision);
                Assert.Equal(3, output.Sends);
            }
        }
        finally
        {
            output.ReleaseSend.TrySetResult();
            output.ReleaseDispose.TrySetResult();
        }
    }

    [Fact]
    public async Task SwappingPathIdsBetweenConnectionsRebuildsBothWithoutChangingFrozenRecords()
    {
        using var directory = TemporaryDirectory.Create();
        var output = new Factory();
        await using var runtime = RuntimeTesting.CreateRuntime([new OneShotInputFactory()], directory.Path, outputs: [output]);
        runtime.StartCore();
        await using var first = runtime.Prepare(new RuntimePlan(1, [Input(), Output("a", "same", connection: 1), Output("b", "same", connection: 2)]));
        await runtime.ActivateAsync(first);
        await UntilAsync(() => runtime.Deliveries.GetStatus().CommittedRecords == 2 && output.Created.Count == 2);
        await using var next = runtime.Prepare(new RuntimePlan(2, [Input(), Output("b", "same", connection: 1), Output("a", "same", connection: 2)]));
        await runtime.ActivateCommittedAsync(next, await runtime.QuiesceInputsAsync(Token));
        await UntilAsync(() => runtime.Deliveries.GetStatus().CommittedRecords == 4 && output.Created.Count == 4);
        Assert.Equal(2, output.Disposed.Count);
        for (var connection = 1; connection <= 2; connection++)
        { Assert.Equal<long>([1, 2], runtime.Deliveries.ReadPending(connection, 0, 10).Select(record => record.Revision)); }
        Assert.DoesNotContain(runtime.GetStatus().Units, unit => unit.State == RuntimeUnitState.BuildFailed);
    }

    [Fact]
    public async Task RemovingDrainedOutputsStopsThemAndKeepsTheQueueWriterRunning()
    {
        using var directory = TemporaryDirectory.Create();
        var output = new Factory { Result = OutputSendStatus.Ack };
        await using var runtime = RuntimeTesting.CreateRuntime([new OneShotInputFactory()], directory.Path, outputs: [output]);
        runtime.StartCore();
        await using var first = runtime.Prepare(Revision(1, Output("old", "old")));
        await runtime.ActivateAsync(first);
        await UntilAsync(() => runtime.Deliveries.GetStatus().DeletedRecords == 1);
        await using var next = runtime.Prepare(new RuntimePlan(2, []));
        await runtime.ActivateCommittedAsync(next, await runtime.QuiesceInputsAsync(Token));
        Assert.Equal(2, runtime.ActiveRevision);
        Assert.Single(output.Disposed);
        var writer = Assert.Single(runtime.GetStatus().Units);
        Assert.Equal("delivery.writer", writer.Id.Kind);
        Assert.Equal(RuntimeUnitState.Running, writer.State);
        Assert.Equal(0, runtime.InFlight.Status.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InputOnlyRevisionWaitsForRetiredConnectionDisposal(bool removedInIntermediateRevision)
    {
        using var directory = TemporaryDirectory.Create();
        var input = new OneShotInputFactory();
        var output = new Factory { BlockOld = true, Result = OutputSendStatus.Ack };
        var observed = new ObservedInputFactory(input, () => !output.Disposed.Contains("old"));
        await using var runtime = RuntimeTesting.CreateRuntime([observed], directory.Path, outputs: [output],
            options: new RuntimeOptions { UnitStopTimeout = TimeSpan.FromMilliseconds(50) });
        runtime.StartCore();
        await using var first = runtime.Prepare(Revision(1, Output("old", "old", metadata: true)));
        await runtime.ActivateAsync(first);
        try
        {
            await output.Sending.Task.WaitAsync(Deadline, Token);
            output.ReleaseSend.TrySetResult();
            await UntilAsync(() => runtime.Deliveries.GetStatus().DeletedRecords == 1);
            if (removedInIntermediateRevision)
            {
                await using var removed = runtime.Prepare(new RuntimePlan(2, []));
                await runtime.ActivateCommittedAsync(removed, await runtime.QuiesceInputsAsync(Token));
                Assert.Equal(2, runtime.ActiveRevision);
            }
            var revision = removedInIntermediateRevision ? 3 : 2;
            await using var next = runtime.Prepare(new RuntimePlan(revision, [Input()]));
            await runtime.ActivateCommittedAsync(next, await runtime.QuiesceInputsAsync(Token));
            await output.Disposing.Task.WaitAsync(Deadline, Token);
            Assert.Equal(1, input.Started);
            Assert.Equal(revision, runtime.ActiveRevision);
            output.ReleaseDispose.TrySetResult();
            await UntilAsync(() => input.Started == 2);
            Assert.Equal(0, observed.OverlappingRuns);
            Assert.Single(output.Created);
            Assert.True((await runtime.QuiesceInputsAsync(Token)).Succeeded);
        }
        finally
        {
            output.ReleaseSend.TrySetResult();
            output.ReleaseDispose.TrySetResult();
        }
    }

    [Fact]
    public async Task NewSenderStartupFailureKeepsCommittedRevisionAndNewInputDeliveries()
    {
        using var directory = TemporaryDirectory.Create();
        var output = new Factory { Result = OutputSendStatus.Ack, FailNewAdapter = true };
        await using var runtime = RuntimeTesting.CreateRuntime([new OneShotInputFactory()], directory.Path, outputs: [output],
            options: new RuntimeOptions { InitialRestartDelay = TimeSpan.FromSeconds(1), RestartJitter = 0 });
        runtime.StartCore();
        await using var first = runtime.Prepare(Revision(1, Output("old", "old")));
        await runtime.ActivateAsync(first);
        await UntilAsync(() => runtime.Deliveries.GetStatus().DeletedRecords == 1);
        await using var next = runtime.Prepare(Revision(2, Output("new", "fault")));
        await runtime.ActivateCommittedAsync(next, await runtime.QuiesceInputsAsync(Token));
        await UntilAsync(() => runtime.Deliveries.GetStatus().CommittedRecords == 2 &&
            runtime.GetStatus().Units.Any(unit => unit.Id.Key == "new" && unit.State == RuntimeUnitState.Backoff));
        Assert.Equal(2, runtime.ActiveRevision);
        Assert.Equal(2, Assert.Single(runtime.Deliveries.ReadPending(1, 0, 10)).Revision);
    }

    [Fact]
    public async Task StaleOrCopiedQuiescenceCannotSwitchRevisionOrConsumePreparedPlan()
    {
        using var directory = TemporaryDirectory.Create();
        await using var runtime = RuntimeTesting.CreateRuntime([], directory.Path);
        runtime.StartCore();
        await using var first = runtime.Prepare(new RuntimePlan(1, []));
        await runtime.ActivateAsync(first);
        var old = await runtime.QuiesceInputsAsync(Token);
        await runtime.RestoreInputsAsync();
        await using var next = runtime.Prepare(new RuntimePlan(2, []));
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.ActivateCommittedAsync(next, old));
        var current = await runtime.QuiesceInputsAsync(Token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.ActivateCommittedAsync(next, current with { }));
        Assert.Equal(1, runtime.ActiveRevision);
        await runtime.ActivateCommittedAsync(next, current);
        Assert.Equal(2, runtime.ActiveRevision);
    }

    private static RuntimePlan Revision(long revision, RuntimeUnitPlan output) => new(revision, [Input(), output]);
    private static RuntimeUnitPlan Output(string id, string version, bool coupled = false, bool metadata = false, long connection = 1) =>
        new(RuntimeUnitId.Create("output.test", id), JsonSerializer.SerializeToElement(new { version, coupled, metadata, connection }));

    private sealed class ObservedInputFactory(OneShotInputFactory source, Func<bool> oldConnectionAlive) : IInputUnitFactory
    {
        public string Kind => source.Kind;
        public int OverlappingRuns;
        public IRuntimeUnit Build(RuntimeUnitPlan plan, InputUnitContext context) => new Unit(this, source.Build(plan, context));
        private sealed class Unit(ObservedInputFactory factory, IRuntimeUnit inner) : IRuntimeUnit
        {
            public RuntimeUnitId Id => inner.Id;
            public Task RunAsync(CancellationToken stoppingToken)
            {
                factory.Observe();
                return inner.RunAsync(stoppingToken);
            }
            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
        private void Observe()
        {
            if (source.Started != 0 && oldConnectionAlive())
            { Interlocked.Increment(ref OverlappingRuns); }
        }
    }

    private sealed class Factory : IOutputAdapterFactory
    {
        public string Kind => "output.test";
        public bool BlockOld { get; init; }
        public bool FailNewAdapter { get; init; }
        public OutputSendStatus Result { get; init; } = OutputSendStatus.SuspendTarget;
        public ConcurrentQueue<string> Created { get; } = new();
        public ConcurrentQueue<string> Disposed { get; } = new();
        public TaskCompletionSource Sending { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseSend { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Disposing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseDispose { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Sends;
        public IOutputTarget Build(RuntimeUnitPlan plan) => new Target(this, plan);
        public RuntimeUnitId? RecoveryGroup(RuntimeUnitPlan plan) => plan.Configuration.GetProperty("metadata").GetBoolean() ? Input().Id : null;

        private sealed class Target(Factory factory, RuntimeUnitPlan plan) : IOutputTarget
        {
            public RuntimeUnitId Id => plan.Id;
            public long ConnectionId => plan.Configuration.GetProperty("connection").GetInt64();
            public string Version => plan.Configuration.GetProperty("version").GetString()!;
            public bool QuiesceWithInputs => plan.Configuration.GetProperty("coupled").GetBoolean();
            public IReadOnlyList<string> Inputs => ["meter/sim"];
            public string? ClientId => null;
            public string Recipient => "test";
            public string CredentialVersion => "credential";
            public string CurrentFingerprint => "fingerprint";
            public bool CanShareSenderWith(IOutputTarget other) => other is Target target && Version == target.Version &&
                !QuiesceWithInputs && !target.QuiesceWithInputs;
            public OutputMessage CreateMessage(EdgeMessage message, Guid deliveryId) =>
                new(StandardEnvelope.Serialize(message, deliveryId), "frozen-topic", 1, CurrentFingerprint, "test");
            public IOutputAdapter CreateAdapter()
            {
                if (factory.FailNewAdapter && Version == "fault")
                { throw new InvalidOperationException("injected-adapter-startup-fault"); }
                factory.Created.Enqueue(Version);
                return new Adapter(factory, Version);
            }
        }
        private sealed class Adapter(Factory factory, string version) : IOutputAdapter
        {
            public async Task<OutputSendResult> SendAsync(OutputMessage message, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref factory.Sends);
                if (version == "old" && factory.BlockOld)
                {
                    factory.Sending.TrySetResult();
                    await factory.ReleaseSend.Task;
                }
                return new(factory.Result);
            }
            public async ValueTask DisposeAsync()
            {
                if (version == "old" && factory.BlockOld)
                {
                    factory.Disposing.TrySetResult();
                    await factory.ReleaseDispose.Task;
                }
                factory.Disposed.Enqueue(version);
            }
        }
    }
}
