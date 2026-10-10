using Microsoft.Extensions.Logging.Abstractions;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Plans;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.Runtime.Diagnostics;
using TenonEdge.Runtime.Lifecycle;

namespace TenonEdge.IntegrationTests.Runtime;

public sealed class DeferredRestartTests
{
    private static readonly RuntimeUnitId Id = RuntimeUnitId.Create("input.test", "meter/line");
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task RenamedSenderWaitsForConnectionOwnerAndPreservesItsLateOutcome()
    {
        var supervisor = Create();
        var group = RuntimeUnitId.Create("sender.connection", "1");
        var renamed = RuntimeUnitId.Create("output.test", "new-path");
        var old = new GatedUnit();
        var replacement = new WaitingUnit(renamed);
        var staleBuilds = 0;
        var outcome = 0;
        Assert.True(supervisor.TryStart(old, () => old, null, group));
        try
        {
            await old.Started.Task.WaitAsync(Deadline, Token);
            Assert.False(supervisor.TryRestartStopped(renamed, () => replacement, null, group));
            Assert.Single(await supervisor.StopAsync(_ => true, TimeSpan.FromMilliseconds(50), Token));
            Assert.True(supervisor.TryRestartStopped(renamed, () => { staleBuilds++; return replacement; }, null, group));
            Assert.True(supervisor.TryRestartStopped(renamed, () =>
            {
                Assert.Equal(42, Volatile.Read(ref outcome));
                return replacement;
            }, null, group));
            var bypass = new WaitingUnit(RuntimeUnitId.Create("output.test", "bypass"));
            Assert.False(supervisor.TryStart(bypass, () => bypass, null, group));
            await bypass.DisposeAsync();
            old.Exit.TrySetResult();
            await old.Disposing.Task.WaitAsync(Deadline, Token);
            Assert.False(replacement.Started.Task.IsCompleted);
            Volatile.Write(ref outcome, 42); // Old send/disposal can still publish an ACK before complete exit.
            old.Disposed.TrySetResult();
            await replacement.Started.Task.WaitAsync(Deadline, Token);
            Assert.Equal(0, staleBuilds);
            supervisor.ForgetStopped();
            Assert.Equal(renamed, Assert.Single(supervisor.Snapshot()).Id);
        }
        finally
        {
            old.Exit.TrySetResult();
            old.Disposed.TrySetResult();
            Assert.Empty(await supervisor.StopAsync(_ => true, Deadline, Token));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangedAliasAndConnectionWaitForBothIdentityAndResourcePredecessors(bool identityExitsFirst)
    {
        var supervisor = Create();
        var firstGroup = RuntimeUnitId.Create("sender.connection", "1");
        var secondGroup = RuntimeUnitId.Create("sender.connection", "2");
        var first = new GatedUnit();
        var second = new GatedUnit(RuntimeUnitId.Create("output.test", "second"));
        var replacement = new WaitingUnit(Id);
        var released = identityExitsFirst ? first : second;
        var blocked = identityExitsFirst ? second : first;
        var builds = 0;
        Assert.True(supervisor.TryStart(first, () => first, null, firstGroup));
        Assert.True(supervisor.TryStart(second, () => second, null, secondGroup));
        try
        {
            await Task.WhenAll(first.Started.Task, second.Started.Task).WaitAsync(Deadline, Token);
            Assert.Equal(2, (await supervisor.StopAsync(_ => true, TimeSpan.FromMilliseconds(50), Token)).Count);
            Assert.True(supervisor.TryRestartStopped(Id, () =>
            {
                Interlocked.Increment(ref builds);
                Assert.Equal(1, Volatile.Read(ref first.DisposalCompleted));
                Assert.Equal(1, Volatile.Read(ref second.DisposalCompleted));
                return replacement;
            }, null, secondGroup));
            // Updating only the factory is allowed; changing its dependencies requires explicit revocation.
            Assert.False(supervisor.TryRestartStopped(Id, () => replacement, null, firstGroup));
            released.Exit.TrySetResult();
            released.Disposed.TrySetResult();
            await released.Disposing.Task.WaitAsync(Deadline, Token);
            Assert.False(replacement.Started.Task.IsCompleted);
            blocked.Exit.TrySetResult();
            blocked.Disposed.TrySetResult();
            await replacement.Started.Task.WaitAsync(Deadline, Token);
            Assert.Equal(1, builds);
            Assert.True(supervisor.IsAlive(Id));
        }
        finally
        {
            first.Exit.TrySetResult();
            first.Disposed.TrySetResult();
            second.Exit.TrySetResult();
            second.Disposed.TrySetResult();
            Assert.Empty(await supervisor.StopAsync(_ => true, Deadline, Token));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RevokingPendingAliasAllowsLatestAliasWithoutResurrectingTheOldOne(bool revokeLatest)
    {
        var supervisor = Create();
        var group = RuntimeUnitId.Create("sender.connection", "1");
        var old = new GatedUnit();
        var obsolete = new WaitingUnit(RuntimeUnitId.Create("output.test", "obsolete"));
        var latest = new WaitingUnit(RuntimeUnitId.Create("output.test", "latest"));
        Assert.True(supervisor.TryStart(old, () => old, null, group));
        try
        {
            await old.Started.Task.WaitAsync(Deadline, Token);
            Assert.Single(await supervisor.StopAsync(_ => true, TimeSpan.FromMilliseconds(50), Token));
            Assert.True(supervisor.TryRestartStopped(obsolete.Id, () => obsolete, null, group));
            Assert.False(supervisor.TryRestartStopped(latest.Id, () => latest, null, group));
            Assert.Empty(await supervisor.StopAsync(id => id == obsolete.Id, Deadline, Token));
            Assert.True(supervisor.TryRestartStopped(latest.Id, () => latest, null, group));
            if (revokeLatest)
            { Assert.Empty(await supervisor.StopAsync(id => id == latest.Id, Deadline, Token)); }
            old.Exit.TrySetResult();
            old.Disposed.TrySetResult();
            if (revokeLatest)
            {
                Assert.Empty(await supervisor.StopAsync(_ => true, Deadline, Token));
                Assert.False(latest.Started.Task.IsCompleted);
            }
            else
            { await latest.Started.Task.WaitAsync(Deadline, Token); }
            Assert.False(obsolete.Started.Task.IsCompleted);
        }
        finally
        {
            old.Exit.TrySetResult();
            old.Disposed.TrySetResult();
            Assert.Empty(await supervisor.StopAsync(_ => true, Deadline, Token));
        }
    }

    [Fact]
    public async Task StopDuringDeferredBuildDisposesTheResultWithoutRunningIt()
    {
        var supervisor = Create();
        using var release = new ManualResetEventSlim();
        var building = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var replacement = new WaitingUnit(Id);
        Assert.True(supervisor.TryRestartStopped(Id, () =>
        {
            building.TrySetResult();
            Assert.True(release.Wait(Deadline));
            return replacement;
        }, null));
        try
        {
            await building.Task.WaitAsync(Deadline, Token);
            Assert.Single(await supervisor.StopAsync(_ => true, TimeSpan.FromMilliseconds(50), Token));
        }
        finally { release.Set(); }
        Assert.Empty(await supervisor.StopAsync(_ => true, Deadline, Token));
        Assert.False(replacement.Started.Task.IsCompleted);
        Assert.Equal(1, replacement.Disposals);
    }

    [Fact]
    public async Task TimeoutReplacementWaitsForBothLoopAndDisposalAndUsesLatestFactory()
    {
        var supervisor = Create();
        var old = new GatedUnit();
        var obsolete = 0;
        var builds = 0;
        var replacement = new WaitingUnit(Id);
        Assert.True(supervisor.TryStart(old, () => throw new InvalidOperationException(), null));
        try
        {
            await old.Started.Task.WaitAsync(Deadline, Token);
            Assert.Equal(Id, Assert.Single(await supervisor.StopAsync(_ => true, TimeSpan.FromMilliseconds(50), Token)));
            Assert.True(supervisor.TryRestartStopped(Id, () => { Interlocked.Increment(ref obsolete); return replacement; }, null));
            Assert.True(supervisor.TryRestartStopped(Id, () => { Interlocked.Increment(ref builds); return replacement; }, null));
            Assert.Equal(0, builds);
            Assert.Equal(0, obsolete);
            Assert.Equal(RuntimeUnitState.StopTimedOut, Assert.Single(supervisor.Snapshot()).State);
            old.Exit.TrySetResult();
            await old.Disposing.Task.WaitAsync(Deadline, Token);
            Assert.True(supervisor.IsAlive(Id));
            Assert.Equal(0, builds);
            Assert.False(replacement.Started.Task.IsCompleted);
            old.Disposed.TrySetResult();
            await replacement.Started.Task.WaitAsync(Deadline, Token);
            Assert.Equal(0, obsolete);
            Assert.Equal(1, builds);
            Assert.Equal(RuntimeUnitState.Running, Assert.Single(supervisor.Snapshot()).State);
        }
        finally
        {
            old.Exit.TrySetResult();
            old.Disposed.TrySetResult();
            Assert.Empty(await supervisor.StopAsync(_ => true, Deadline, Token));
        }
    }

    [Fact]
    public async Task StopRevokesQueuedReplacementEvenIfOldLoopExitsLater()
    {
        var supervisor = Create();
        var old = new GatedUnit();
        var builds = 0;
        supervisor.TryStart(old, () => throw new InvalidOperationException(), null);
        try
        {
            await old.Started.Task.WaitAsync(Deadline, Token);
            Assert.Single(await supervisor.StopAsync(_ => true, TimeSpan.FromMilliseconds(50), Token));
            Assert.True(supervisor.TryRestartStopped(Id, () => { Interlocked.Increment(ref builds); return new WaitingUnit(Id); }, null));
            Assert.Single(await supervisor.StopAsync(_ => true, TimeSpan.FromMilliseconds(50), Token));
            old.Exit.TrySetResult();
            old.Disposed.TrySetResult();
            Assert.Empty(await supervisor.StopAsync(_ => true, Deadline, Token));
            Assert.False(supervisor.IsAlive(Id));
            Assert.Equal(0, builds);
            Assert.Equal(RuntimeUnitState.Stopped, Assert.Single(supervisor.Snapshot()).State);
        }
        finally
        {
            old.Exit.TrySetResult();
            old.Disposed.TrySetResult();
            await supervisor.StopAsync(_ => true, Deadline, Token);
        }
    }

    [Fact]
    public async Task RunningUnitCannotBeReplacedAndQueuedUnitCannotBeBypassed()
    {
        var supervisor = Create();
        var old = new GatedUnit();
        var replacement = new WaitingUnit(Id);
        supervisor.TryStart(old, () => throw new InvalidOperationException(), null);
        try
        {
            await old.Started.Task.WaitAsync(Deadline, Token);
            Assert.False(supervisor.TryRestartStopped(Id, () => replacement, null));
            Assert.Single(await supervisor.StopAsync(_ => true, TimeSpan.FromMilliseconds(50), Token));
            Assert.True(supervisor.TryRestartStopped(Id, () => replacement, null));
            var unwanted = new WaitingUnit(Id);
            Assert.False(supervisor.TryStart(unwanted, () => unwanted, null));
            await unwanted.DisposeAsync();
            old.Exit.TrySetResult();
            old.Disposed.TrySetResult();
            await replacement.Started.Task.WaitAsync(Deadline, Token);
            Assert.False(unwanted.Started.Task.IsCompleted);
        }
        finally
        {
            old.Exit.TrySetResult();
            old.Disposed.TrySetResult();
            Assert.Empty(await supervisor.StopAsync(_ => true, Deadline, Token));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeferredFactoryFailureIsSupervisedAndWrongIdentityNeverRuns(bool wrongIdentity)
    {
        var supervisor = Create();
        var builds = 0;
        var replacement = new WaitingUnit(Id);
        var wrong = new WaitingUnit(RuntimeUnitId.Create("input.test", "other/line"));
        Assert.True(supervisor.TryRestartStopped(Id, () =>
        {
            if (Interlocked.Increment(ref builds) == 1)
            {
                if (wrongIdentity)
                { return wrong; }
                throw new InvalidOperationException("fixture-sensitive-detail");
            }
            return replacement;
        }, null));
        try
        {
            await replacement.Started.Task.WaitAsync(Deadline, Token);
            Assert.Equal(2, builds);
            Assert.False(wrong.Started.Task.IsCompleted);
            Assert.Equal(wrongIdentity ? 1 : 0, wrong.Disposals);
            Assert.Equal(1, Assert.Single(supervisor.Snapshot()).RestartCount);
            Assert.DoesNotContain("fixture-sensitive-detail", Assert.Single(supervisor.Snapshot()).LastError!, StringComparison.Ordinal);
        }
        finally { Assert.Empty(await supervisor.StopAsync(_ => true, Deadline, Token)); }
    }

    private static UnitSupervisor Create() => new(new RuntimeOptions
    { InitialRestartDelay = TimeSpan.FromMilliseconds(10), RestartJitter = 0 }, TimeProvider.System,
        new RecordingEventSink(), NullLogger.Instance, () => 0);

    private sealed class GatedUnit(RuntimeUnitId? id = null) : IRuntimeUnit
    {
        public RuntimeUnitId Id => id ?? DeferredRestartTests.Id;
        public int DisposalCompleted;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Exit { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Disposing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task RunAsync(CancellationToken stoppingToken) { Started.TrySetResult(); await Exit.Task; }
        public async ValueTask DisposeAsync()
        { Disposing.TrySetResult(); await Disposed.Task; Volatile.Write(ref DisposalCompleted, 1); }
    }

    private sealed class WaitingUnit(RuntimeUnitId id) : IRuntimeUnit
    {
        public RuntimeUnitId Id => id;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Disposals;
        public async Task RunAsync(CancellationToken stoppingToken)
        { Started.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken); }
        public ValueTask DisposeAsync() { Interlocked.Increment(ref Disposals); return ValueTask.CompletedTask; }
    }
}
