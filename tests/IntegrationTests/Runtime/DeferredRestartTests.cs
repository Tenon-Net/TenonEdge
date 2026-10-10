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

    private sealed class GatedUnit : IRuntimeUnit
    {
        public RuntimeUnitId Id => DeferredRestartTests.Id;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Exit { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Disposing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task RunAsync(CancellationToken stoppingToken) { Started.TrySetResult(); await Exit.Task; }
        public async ValueTask DisposeAsync() { Disposing.TrySetResult(); await Disposed.Task; }
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
