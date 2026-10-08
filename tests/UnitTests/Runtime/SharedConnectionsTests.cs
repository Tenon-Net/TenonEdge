using TenonEdge.Abstractions.Contracts;
using TenonEdge.Runtime.Connections;

namespace TenonEdge.UnitTests.Runtime;

public sealed class SharedConnectionsTests
{
    [Fact]
    public async Task BorrowersShareOneSessionAndOnlyLastReleaseDisposesIt()
    {
        var manager = new SharedConnections();
        var created = 0;
        var session = new Session();
        var first = manager.Acquire(1, "mqtt", "same", () => { created++; return session; });
        var second = manager.Acquire(1, "mqtt", "same", () => { created++; return new Session(); });
        Assert.Equal(1, created);
        Assert.Same(session, await second.ExecuteAsync((value, _) => Task.FromResult(value), Token));
        await first.DisposeAsync();
        Assert.Equal(0, session.Disposals);
        await second.DisposeAsync();
        await second.DisposeAsync();
        Assert.Equal(1, session.Disposals);
        await using var replacement = manager.Acquire(1, "mqtt", "changed", () => new Session());
    }

    [Fact]
    public async Task OperationsSerializeAcrossBorrowersAndCancelledWaitDoesNotRun()
    {
        var manager = new SharedConnections();
        await using var first = manager.Acquire(1, "modbus", "same", () => new Session());
        await using var second = manager.Acquire(1, "modbus", "same", () => new Session());
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = first.ExecuteAsync(async (_, _) => { started.SetResult(); await release.Task; return 1; }, Token);
        await started.Task;
        using var cancellation = new CancellationTokenSource();
        var ran = false;
        var waiting = second.ExecuteAsync((_, _) => { ran = true; return Task.FromResult(2); }, cancellation.Token);
        Assert.False(waiting.IsCompleted);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.False(ran);
        release.SetResult();
        Assert.Equal(1, await running);
        Assert.Equal(3, await second.ExecuteAsync((_, _) => Task.FromResult(3), Token));
    }

    [Fact]
    public async Task ClosingWaitsForActualOperationExitAndDoesNotPermitSecondSession()
    {
        var manager = new SharedConnections();
        var session = new Session();
        var lease = manager.Acquire(1, "mqtt", "same", () => session);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = lease.ExecuteAsync(async (_, _) => { await release.Task; return true; }, Token);
        var disposing = lease.DisposeAsync().AsTask();
        Assert.False(disposing.IsCompleted);
        Assert.Same(disposing, lease.DisposeAsync().AsTask());
        Assert.Equal(0, session.Disposals);
        Assert.Throws<InvalidOperationException>(() => manager.Acquire(1, "mqtt", "same", () => new Session()));
        release.SetResult();
        Assert.True(await running);
        await disposing;
        Assert.Equal(1, session.Disposals);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => lease.ExecuteAsync((_, _) => Task.FromResult(1), Token));
        await using var replacement = manager.Acquire(1, "mqtt", "same", () => new Session());
    }

    [Fact]
    public async Task IncompatibleConfigurationIsRejectedWithoutRunningTheFactory()
    {
        var manager = new SharedConnections();
        await using var first = manager.Acquire(1, "mqtt", "same", () => new Session());
        Assert.Throws<InvalidOperationException>(() => manager.Acquire<Session>(1, "mqtt", "changed", () => throw new InvalidOperationException("must not build")));
        Assert.Throws<InvalidOperationException>(() => manager.Acquire(1, "modbus", "same", () => new Session()));
        await using var independent = manager.Acquire(2, "mqtt", "changed", () => new Session());
        Assert.Equal(42, await independent.ExecuteAsync((_, _) => Task.FromResult(42), Token));
    }

    [Fact]
    public async Task FailedDisposalKeepsIdentityReservedRatherThanCreatingASecondConnection()
    {
        var manager = new SharedConnections();
        var lease = manager.Acquire(1, "mqtt", "same", () => new FailedDisposal());
        await Assert.ThrowsAsync<IOException>(() => lease.DisposeAsync().AsTask());
        await Assert.ThrowsAsync<IOException>(() => lease.DisposeAsync().AsTask());
        Assert.Throws<InvalidOperationException>(() => manager.Acquire(1, "mqtt", "same", () => new FailedDisposal()));
    }

    [Fact]
    public async Task CapacityRejectsNewSessionsButAllowsBorrowingAndReusesReleasedSlots()
    {
        var manager = new SharedConnections();
        var leases = new List<IConnectionLease<Session>>();
        try
        {
            for (var id = 1; id <= SharedConnections.MaximumConnections; id++)
            {
                leases.Add(manager.Acquire(id, "mqtt", "same", () => new Session()));
            }
            var created = false;
            Assert.Throws<InvalidOperationException>(() => manager.Acquire(501, "mqtt", "same", () => { created = true; return new Session(); }));
            Assert.False(created);
            await using (var borrowed = manager.Acquire<Session>(1, "mqtt", "same", () => throw new InvalidOperationException()))
            {
                Assert.Equal(42, await borrowed.ExecuteAsync((_, _) => Task.FromResult(42), Token));
            }
            await leases[0].DisposeAsync();
            leases.Add(manager.Acquire(501, "mqtt", "same", () => new Session()));
        }
        finally
        {
            foreach (var lease in leases)
            { await lease.DisposeAsync(); }
        }
    }

    private sealed class FailedDisposal : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.FromException(new IOException("test-disposal"));
    }

    private sealed class Session : IAsyncDisposable
    {
        public int Disposals { get; private set; }
        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
    }
    private static CancellationToken Token => TestContext.Current.CancellationToken;
}
