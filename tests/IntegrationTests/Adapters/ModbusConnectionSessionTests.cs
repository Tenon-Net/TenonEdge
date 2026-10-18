using System.Net;
using Microsoft.Extensions.Time.Testing;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Adapters.Modbus;
using TenonEdge.IntegrationTests.Probes.Modbus;
using TenonEdge.Runtime.Connections;

namespace TenonEdge.IntegrationTests.Adapters;

public sealed class ModbusConnectionSessionTests
{
    [Fact]
    public async Task ObservationRequiresActualBusinessReadsAndSingleSlaveTimeoutDoesNotDegradeConnection()
    {
        await using var slave = ModbusTestSlave.Start(ModbusFraming.Tcp,
            request => request.Unit == 2 ? new SlaveReply { Silent = true } : SlaveReply.Normal);
        await using var session = new ModbusConnectionSession(Settings(slave), [1, 2], Connector(), TimeProvider.System);
        Assert.Equal(new(ConnectionCommunicationState.Connecting), session.Observation);
        Assert.True((await session.TestConnectionAsync(Token)).Success);
        Assert.Equal(ModbusRequestOutcome.Success, (await session.ReadPreviewAsync(1, 3, 0, 1, Token)).Outcome);
        Assert.Equal(new(ConnectionCommunicationState.Connecting), session.Observation);
        Assert.Equal(ModbusRequestOutcome.Timeout, (await session.ReadAsync(2, 3, 0, 1, Token)).Outcome);
        Assert.Equal(ConnectionCommunicationState.Connecting, session.Observation.State);
        Assert.Equal(ModbusRequestOutcome.Success, (await session.ReadAsync(1, 3, 0, 1, Token)).Outcome);
        var healthy = session.Observation;
        Assert.Equal(ConnectionCommunicationState.Connected, healthy.State);
        Assert.NotNull(healthy.LastSuccessAt);
        Assert.Equal(ModbusRequestOutcome.Timeout, (await session.ReadAsync(2, 3, 0, 1, Token)).Outcome);
        Assert.Equal(healthy, session.Observation);
        Assert.Equal(1, slave.ConnectionCount);
    }

    [Fact]
    public async Task AllSlaveTimeoutsDegradeOnlyAfterAllRequestedAndRecoveryRetainsLastError()
    {
        await using var slave = ModbusTestSlave.Start(ModbusFraming.Tcp,
            request => new SlaveReply { Silent = request.Sequence <= 2 });
        var time = new FakeTimeProvider();
        await using var session = new ModbusConnectionSession(Settings(slave), [1, 2], Connector(), time);
        Assert.Equal(ModbusRequestOutcome.Timeout, (await session.ReadAsync(1, 3, 0, 1, Token)).Outcome);
        Assert.Null(session.Observation.LastErrorAt);
        Assert.Equal(ModbusRequestOutcome.Timeout, (await session.ReadAsync(2, 3, 0, 1, Token)).Outcome);
        var failed = session.Observation;
        Assert.Equal(ConnectionCommunicationState.Degraded, failed.State);
        Assert.Equal("modbus.allUnitsTimedOut", failed.LastErrorCode);
        Assert.Equal(time.GetUtcNow(), failed.LastErrorAt);
        Assert.False((await session.ReadAsync(1, 3, 0, 1, Token)).WasRequested);
        Assert.Equal(failed, session.Observation);
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(ModbusRequestOutcome.Success, (await session.ReadAsync(1, 3, 0, 1, Token)).Outcome);
        Assert.Equal(ConnectionCommunicationState.Connected, session.Observation.State);
        Assert.Equal(time.GetUtcNow(), session.Observation.LastSuccessAt);
        Assert.Equal(failed.LastErrorAt, session.Observation.LastErrorAt);
        Assert.Equal(failed.LastErrorCode, session.Observation.LastErrorCode);
        Assert.Equal(2, slave.ConnectionCount);
    }

    [Fact]
    public async Task RtuEofDuringDrainIsAConnectionFailureNotJustOneSlaveTimeout()
    {
        await using var slave = ModbusTestSlave.Start(ModbusFraming.RtuOverTcp,
            request => request.Sequence == 1 ? SlaveReply.Normal : new SlaveReply { Delay = TimeSpan.FromMilliseconds(700), CloseConnection = true });
        await using var session = new ModbusConnectionSession(Settings(slave, true) with { RtuSilenceMs = 1000 }, [1, 2], Connector(), TimeProvider.System);
        Assert.Equal(ModbusRequestOutcome.Success, (await session.ReadAsync(1, 3, 0, 1, Token)).Outcome);
        Assert.Equal(ModbusRequestOutcome.Timeout, (await session.ReadAsync(2, 3, 0, 1, Token)).Outcome);
        Assert.Equal(ConnectionCommunicationState.Degraded, session.Observation.State);
        Assert.Equal("modbus.connectionFailed", session.Observation.LastErrorCode);
        Assert.NotNull(session.Observation.LastSuccessAt);
    }

    [Fact]
    public async Task TransportFailureIsConnectionLevelEvenWhenAnotherSlaveHasNotBeenRead()
    {
        await using var slave = ModbusTestSlave.Start(ModbusFraming.Tcp, _ => new SlaveReply { CloseConnection = true });
        await using var session = new ModbusConnectionSession(Settings(slave), [1, 2], Connector(), TimeProvider.System);
        Assert.Equal(ModbusRequestOutcome.ConnectionError, (await session.ReadAsync(1, 3, 0, 1, Token)).Outcome);
        Assert.Equal(ConnectionCommunicationState.Degraded, session.Observation.State);
        Assert.Equal("modbus.connectionFailed", session.Observation.LastErrorCode);
        Assert.NotNull(session.Observation.LastErrorAt);
        Assert.Null(session.Observation.LastSuccessAt);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SilentSlaveDoesNotResetHealthySiblingSharedConnection(bool rtu)
    {
        await using var slave = ModbusTestSlave.Start(rtu ? ModbusFraming.RtuOverTcp : ModbusFraming.Tcp,
            request => new SlaveReply { Silent = request.Unit == 1 });
        var settings = Settings(slave, rtu);
        var shared = new SharedConnections();
        var created = 0;
        ModbusConnectionSession Create()
        {
            created++;
            return new(settings, [1, 2], Connector(), TimeProvider.System);
        }
        await using var first = shared.Acquire(1, "modbus", settings.Identity, Create);
        await using var second = shared.Acquire(1, "modbus", settings.Identity, Create);
        Assert.Equal(ModbusRequestOutcome.Timeout, (await Read(first, 1)).Outcome);
        var healthy = await Read(second, 2);
        Assert.Equal(ModbusRequestOutcome.Success, healthy.Outcome);
        Assert.Equal((ushort)4096, Assert.Single(healthy.Registers!));
        await first.DisposeAsync();
        Assert.Equal(ModbusRequestOutcome.Success, (await Read(second, 2)).Outcome);
        Assert.Equal(1, created);
        Assert.Equal(1, slave.ConnectionCount);
    }

    [Fact]
    public async Task AllSlaveTimeoutsBackOffWithoutFabricatingRequestsThenReconnect()
    {
        await using var slave = ModbusTestSlave.Start(ModbusFraming.Tcp,
            request => new SlaveReply { Silent = request.Sequence <= 2 });
        var time = new FakeTimeProvider();
        await using var session = new ModbusConnectionSession(Settings(slave), [1, 2], Connector(), time);
        Assert.Equal(ModbusRequestOutcome.Timeout, (await session.ReadAsync(1, 3, 0, 1, Token)).Outcome);
        Assert.Equal(ModbusRequestOutcome.Timeout, (await session.ReadAsync(2, 3, 0, 1, Token)).Outcome);
        var skipped = await session.ReadAsync(1, 3, 0, 1, Token);
        Assert.False(skipped.WasRequested);
        Assert.Equal(ModbusRequestOutcome.ConnectionError, skipped.Outcome);
        Assert.Equal(2, slave.RequestCount);
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(ModbusRequestOutcome.Success, (await session.ReadAsync(2, 3, 0, 1, Token)).Outcome);
        Assert.Equal(2, slave.ConnectionCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProtocolErrorsAreNotAllSlaveTimeouts(bool crc)
    {
        await using var slave = ModbusTestSlave.Start(ModbusFraming.RtuOverTcp,
            request => request.Sequence == 1 ? new SlaveReply { CorruptCrc = crc, ExceptionCode = crc ? null : (byte)6 } : SlaveReply.Normal);
        await using var session = new ModbusConnectionSession(Settings(slave, true), [1], Connector(), TimeProvider.System);
        var error = await session.ReadAsync(1, 3, 0, 1, Token);
        Assert.Equal(crc ? ModbusRequestOutcome.ReadError : ModbusRequestOutcome.DeviceException, error.Outcome);
        Assert.Equal(crc ? null : (byte?)6, error.ExceptionCode);
        Assert.Equal(crc ? ConnectionCommunicationState.Degraded : ConnectionCommunicationState.Connected, session.Observation.State);
        Assert.Equal(ModbusRequestOutcome.Success, (await session.ReadAsync(1, 3, 0, 1, Token)).Outcome);
        Assert.Equal(1, slave.ConnectionCount);
    }

    [Fact]
    public async Task LateRtuReplyRequiresImmediateReplacementWithoutBackoff()
    {
        await using var slave = ModbusTestSlave.Start(ModbusFraming.RtuOverTcp,
            request => new SlaveReply { Delay = request.Sequence == 1 ? TimeSpan.FromMilliseconds(700) : TimeSpan.Zero });
        await using var session = new ModbusConnectionSession(Settings(slave, true) with { RtuSilenceMs = 1000 }, [1, 2], Connector(), TimeProvider.System);
        Assert.Equal(ModbusRequestOutcome.Timeout, (await session.ReadAsync(1, 3, 0, 1, Token)).Outcome);
        Assert.Equal(ModbusRequestOutcome.Success, (await session.ReadAsync(2, 3, 0, 1, Token)).Outcome);
        Assert.Equal(2, slave.ConnectionCount);
    }

    [Fact]
    public async Task CancellingActualReadJoinsItAndNextReadUsesNewGeneration()
    {
        await using var slave = ModbusTestSlave.Start(ModbusFraming.Tcp,
            request => new SlaveReply { Silent = request.Sequence == 1 });
        await using var session = new ModbusConnectionSession(Settings(slave) with { ReadTimeoutMs = 30000 }, [1], Connector(), TimeProvider.System);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var read = session.ReadAsync(1, 3, 0, 1, stop.Token);
        await slave.WaitForRequestsAsync(1, TimeSpan.FromSeconds(5), Token);
        await stop.CancelAsync();
        var observed = Record.ExceptionAsync(() => read);
        Assert.IsAssignableFrom<OperationCanceledException>(await observed.AsTask().WaitAsync(TimeSpan.FromSeconds(5), Token));
        Assert.Null(session.Observation.LastErrorAt);
        Assert.Equal(ModbusRequestOutcome.Success, (await session.ReadAsync(1, 3, 0, 1, Token)).Outcome);
        Assert.Equal(2, slave.ConnectionCount);
    }

    [Fact]
    public async Task CancellingRequestIntervalDoesNotAbortHealthyConnection()
    {
        await using var slave = ModbusTestSlave.Start(ModbusFraming.Tcp);
        var time = new FakeTimeProvider();
        await using var session = new ModbusConnectionSession(Settings(slave) with { RequestIntervalMs = 1000 }, [1], Connector(), time);
        Assert.Equal(ModbusRequestOutcome.Success, (await session.ReadAsync(1, 3, 0, 1, Token)).Outcome);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var pending = session.ReadAsync(1, 3, 0, 1, stop.Token);
        Assert.False(pending.IsCompleted);
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(1, slave.RequestCount);
        Assert.Equal(ConnectionCommunicationState.Connected, session.Observation.State);
        Assert.Null(session.Observation.LastErrorCode);
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(ModbusRequestOutcome.Success, (await session.ReadAsync(1, 3, 0, 1, Token)).Outcome);
        Assert.Equal(1, slave.ConnectionCount);
    }

    [Fact]
    public async Task QueuedCancellationAndFinalReleaseDoNotCreateOverlappingSessions()
    {
        await using var slave = ModbusTestSlave.Start(ModbusFraming.Tcp,
            request => new SlaveReply { Silent = request.Sequence == 1 });
        var settings = Settings(slave) with { ReadTimeoutMs = 30000 };
        var shared = new SharedConnections();
        ModbusConnectionSession Create() => new(settings, [1], Connector(), TimeProvider.System);
        await using var first = shared.Acquire(1, "modbus", settings.Identity, Create);
        await using var second = shared.Acquire(1, "modbus", settings.Identity, Create);
        using var activeStop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var active = first.ExecuteAsync((session, token) => session.ReadAsync(1, 3, 0, 1, token), activeStop.Token);
        await slave.WaitForRequestsAsync(1, TimeSpan.FromSeconds(5), Token);
        using var queuedStop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var queued = second.ExecuteAsync((session, token) => session.ReadAsync(1, 3, 0, 1, token), queuedStop.Token);
        await queuedStop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        Assert.False(active.IsCompleted);
        await first.DisposeAsync();
        var finalRelease = second.DisposeAsync().AsTask();
        Assert.False(finalRelease.IsCompleted);
        Assert.Throws<InvalidOperationException>(() => shared.Acquire(1, "modbus", settings.Identity, Create));
        await activeStop.CancelAsync();
        var observed = Record.ExceptionAsync(() => active);
        Assert.IsAssignableFrom<OperationCanceledException>(await observed.AsTask().WaitAsync(TimeSpan.FromSeconds(5), Token));
        await finalRelease.WaitAsync(TimeSpan.FromSeconds(5), Token);
        await using var replacement = shared.Acquire(1, "modbus", settings.Identity, Create);
        Assert.Equal(ModbusRequestOutcome.Success, (await Read(replacement, 1)).Outcome);
        Assert.Equal(2, slave.ConnectionCount);
    }

    [Fact]
    public async Task ConnectionErrorsUseExponentialBackoffAndSuccessResetsDelay()
    {
        await using var slave = ModbusTestSlave.Start(ModbusFraming.Tcp,
            request => new SlaveReply { CloseConnection = request.Sequence != 3 });
        var time = new FakeTimeProvider();
        await using var session = new ModbusConnectionSession(Settings(slave), [1, 2], Connector(), time);
        Assert.Equal(ModbusRequestOutcome.ConnectionError, (await session.ReadAsync(1, 3, 0, 1, Token)).Outcome);
        Assert.False((await session.ReadAsync(2, 3, 0, 1, Token)).WasRequested);
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.True((await session.ReadAsync(2, 3, 0, 1, Token)).WasRequested);
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.False((await session.ReadAsync(1, 3, 0, 1, Token)).WasRequested);
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(ModbusRequestOutcome.Success, (await session.ReadAsync(1, 3, 0, 1, Token)).Outcome);
        Assert.Equal(ModbusRequestOutcome.ConnectionError, (await session.ReadAsync(1, 3, 0, 1, Token)).Outcome);
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.True((await session.ReadAsync(2, 3, 0, 1, Token)).WasRequested);
        Assert.Equal(4, slave.ConnectionCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConnectionTestDoesNotReadAndRuntimeReusesSamePhysicalStream(bool rtu)
    {
        await using var slave = ModbusTestSlave.Start(rtu ? ModbusFraming.RtuOverTcp : ModbusFraming.Tcp);
        var settings = Settings(slave, rtu);
        var shared = new SharedConnections();
        await using var runtime = shared.Acquire(1, "modbus", settings.Identity,
            () => new ModbusConnectionSession(settings, [1, 2], Connector(), TimeProvider.System));
        await using var preview = shared.Acquire<ModbusConnectionSession>(1, "modbus", settings.Identity,
            () => throw new InvalidOperationException("Must borrow runtime session"));
        var connected = await preview.ExecuteAsync((session, token) => session.TestConnectionAsync(token), Token);
        Assert.True(connected.Success);
        Assert.Equal("success", connected.Code);
        Assert.Equal(["success", "success", "skipped", "skipped", "skipped"], connected.Steps.Select(step => step.Status));
        Assert.Equal(0, slave.RequestCount);
        Assert.Equal(ModbusRequestOutcome.Success, (await Read(runtime, 2)).Outcome);
        var reused = await preview.ExecuteAsync((session, token) => session.TestConnectionAsync(token), Token);
        Assert.Equal("connection.reused", reused.Code);
        Assert.Equal("reused", reused.Steps[1].Status);
        Assert.Null(reused.Steps[1].ElapsedMs);
        Assert.Equal(1, slave.RequestCount);
        Assert.Equal(ModbusRequestOutcome.Success, (await Read(runtime, 1)).Outcome);
        Assert.Equal(1, slave.ConnectionCount);
    }

    [Fact]
    public async Task ConnectionTestQueuesBehindRuntimeAndQueuedCancellationPreservesStream()
    {
        await using var slave = ModbusTestSlave.Start(ModbusFraming.Tcp);
        var settings = Settings(slave);
        var shared = new SharedConnections();
        await using var lease = shared.Acquire(1, "modbus", settings.Identity,
            () => new ModbusConnectionSession(settings, [1], Connector(), TimeProvider.System));
        Assert.Equal(ModbusRequestOutcome.Success, (await Read(lease, 1)).Outcome);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holding = lease.ExecuteAsync(async (_, token) =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(token);
            return true;
        }, Token);
        await entered.Task.WaitAsync(Token);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var queued = lease.ExecuteAsync((session, token) => session.TestConnectionAsync(token), stop.Token);
        try
        {
            Assert.False(queued.IsCompleted);
            await stop.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        }
        finally { release.TrySetResult(); }
        await holding;
        Assert.Equal(ModbusRequestOutcome.Success, (await Read(lease, 1)).Outcome);
        Assert.Equal(1, slave.ConnectionCount);
    }

    [Fact]
    public async Task ConnectionTestDoesNotBypassRuntimeReconnectBackoff()
    {
        await using var slave = ModbusTestSlave.Start(ModbusFraming.Tcp, _ => new SlaveReply { CloseConnection = true });
        await using var session = new ModbusConnectionSession(Settings(slave), [1], Connector(), new FakeTimeProvider());
        Assert.Equal(ModbusRequestOutcome.ConnectionError, (await session.ReadAsync(1, 3, 0, 1, Token)).Outcome);
        var result = await session.TestConnectionAsync(Token);
        Assert.False(result.Success);
        Assert.Equal("connection.backoff", result.Code);
        Assert.Equal("notReached", result.Steps[0].Status);
        Assert.Equal("notReached", result.Steps[1].Status);
        Assert.Equal(1, slave.ConnectionCount);
    }

    [Fact]
    public async Task ConnectionTestReportsRealPolicyDenialWithoutProtocolAttempt()
    {
        await using var slave = ModbusTestSlave.Start(ModbusFraming.Tcp);
        await using var session = new ModbusConnectionSession(Settings(slave), [1], new OutboundConnector(new DeniedPolicy()), TimeProvider.System);
        var result = await session.TestConnectionAsync(Token);
        Assert.False(result.Success);
        Assert.Equal("outbound.denied", result.Code);
        Assert.Equal("skipped", result.Steps[4].Status);
        Assert.Equal(0, slave.ConnectionCount);
    }

    [Fact]
    public async Task ConnectionTestDeadlineAndCallerCancellationAreDistinct()
    {
        await using var slave = ModbusTestSlave.Start(ModbusFraming.Tcp);
        var time = new FakeTimeProvider();
        var connector = new WaitingConnector();
        await using var session = new ModbusConnectionSession(Settings(slave), [1], connector, time);
        var pending = session.TestConnectionAsync(Token);
        await connector.Entered.Task.WaitAsync(Token);
        time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal("timeout", (await pending.WaitAsync(TimeSpan.FromSeconds(5), Token)).Code);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var cancelled = session.TestConnectionAsync(stop.Token);
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Assert.Equal(0, slave.ConnectionCount);
    }

    [Theory]
    [InlineData(System.Net.Sockets.SocketError.HostNotFound, "dns.failed", "failed", "notReached")]
    [InlineData(System.Net.Sockets.SocketError.ConnectionRefused, "connection.refused", "success", "failed")]
    public async Task ConnectionTestClassifiesTransportFailures(System.Net.Sockets.SocketError error,
        string code, string addressStatus, string connectionStatus)
    {
        var settings = new ModbusConnectionSettings { ConnectionId = 1, Host = "example.test" };
        await using var session = new ModbusConnectionSession(settings, [1], new FailingConnector(error), TimeProvider.System);
        var result = await session.TestConnectionAsync(Token);
        Assert.False(result.Success);
        Assert.Equal(code, result.Code);
        Assert.Equal(addressStatus, result.Steps[0].Status);
        Assert.Equal(connectionStatus, result.Steps[1].Status);
        Assert.Equal("skipped", result.Steps[4].Status);
    }

    private sealed class FailingConnector(System.Net.Sockets.SocketError error) : IOutboundConnector
    {
        public ValueTask<Stream> ConnectAsync(string host, int port, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Diagnostic overload expected");
        public ValueTask<Stream> ConnectAsync(string host, int port, Action<OutboundConnectionStage> progress, CancellationToken cancellationToken)
        {
            progress(OutboundConnectionStage.Resolving);
            if (error != System.Net.Sockets.SocketError.HostNotFound)
            { progress(OutboundConnectionStage.Connecting); }
            throw new System.Net.Sockets.SocketException((int)error);
        }
    }

    [Fact]
    public async Task TestCreatedSessionBindsRealRuntimeUnitsWithoutPhantomSlaveOrSecondConnection()
    {
        await using var slave = ModbusTestSlave.Start(ModbusFraming.Tcp);
        var settings = Settings(slave);
        var shared = new SharedConnections();
        await using var keep = shared.Acquire(1, "modbus", settings.Identity,
            () => new ModbusConnectionSession(settings, null, Connector(), TimeProvider.System));
        var probe = new ModbusConnectionTest(shared, new WaitingConnector(), TimeProvider.System);
        Assert.True((await probe.RunAsync(settings, Token)).Success);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Read(keep, 2));
        await keep.ExecuteAsync((session, _) =>
        {
            session.ConfigureUnits([2, 3]);
            session.ConfigureUnits([3, 2]);
            Assert.Throws<InvalidOperationException>(() => session.ConfigureUnits([1, 2, 3]));
            return Task.FromResult(true);
        }, Token);
        Assert.Equal(ModbusRequestOutcome.Success, (await Read(keep, 2)).Outcome);
        Assert.Equal(ModbusRequestOutcome.Success, (await Read(keep, 3)).Outcome);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Read(keep, 1));
        Assert.Equal(1, slave.ConnectionCount);
    }

    [Fact]
    public async Task ProbeTotalBudgetBoundsQueueAndDoesNotTouchRuntimeStream()
    {
        await using var slave = ModbusTestSlave.Start(ModbusFraming.Tcp);
        var time = new FakeTimeProvider();
        var settings = Settings(slave);
        var shared = new SharedConnections();
        await using var lease = shared.Acquire(1, "modbus", settings.Identity,
            () => new ModbusConnectionSession(settings, [1], Connector(), time));
        Assert.Equal(ModbusRequestOutcome.Success, (await Read(lease, 1)).Outcome);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holding = lease.ExecuteAsync(async (_, token) =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(token);
            return true;
        }, Token);
        await entered.Task.WaitAsync(Token);
        try
        {
            var probe = new ModbusConnectionTest(shared, new WaitingConnector(), time);
            var result = probe.RunAsync(settings, Token);
            time.Advance(TimeSpan.FromMilliseconds(settings.ConnectTimeoutMs));
            var timedOut = await result.WaitAsync(TimeSpan.FromSeconds(5), Token);
            Assert.Equal("connection.busy", timedOut.Code);
            Assert.Equal("notReached", timedOut.Steps[0].Status);
            Assert.Equal("notReached", timedOut.Steps[1].Status);
        }
        finally { release.TrySetResult(); }
        await holding;
        Assert.Equal(ModbusRequestOutcome.Success, (await Read(lease, 1)).Outcome);
        Assert.Equal(1, slave.ConnectionCount);
    }

    [Fact]
    public async Task RuntimeBindingRepeatedBySiblingInputDoesNotEraseTimeoutHistory()
    {
        await using var slave = ModbusTestSlave.Start(ModbusFraming.Tcp, _ => new SlaveReply { Silent = true });
        await using var session = new ModbusConnectionSession(Settings(slave), null, Connector(), new FakeTimeProvider());
        session.ConfigureUnits([2, 3]);
        Assert.Equal(ModbusRequestOutcome.Timeout, (await session.ReadAsync(2, 3, 0, 1, Token)).Outcome);
        session.ConfigureUnits([3, 2]);
        Assert.Equal(ModbusRequestOutcome.Timeout, (await session.ReadAsync(3, 3, 0, 1, Token)).Outcome);
        Assert.Equal("connection.backoff", (await session.TestConnectionAsync(Token)).Code);
        Assert.False((await session.ReadAsync(2, 3, 0, 1, Token)).WasRequested);
        Assert.Equal(2, slave.RequestCount);
    }

    private sealed class DeniedPolicy : IOutboundAddressPolicy
    {
        public bool IsAllowed(IPAddress address, int port) => false;
    }

    private sealed class WaitingConnector : IOutboundConnector
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<Stream> ConnectAsync(string host, int port, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }
    }

    private static Task<ModbusReadResult> Read(IConnectionLease<ModbusConnectionSession> lease, byte unit) =>
        lease.ExecuteAsync((session, token) => session.ReadAsync(unit, 3, 0, 1, token), Token);

    private static ModbusConnectionSettings Settings(ModbusTestSlave slave, bool rtu = false) => new()
    {
        ConnectionId = 1,
        Host = slave.Endpoint.Address.ToString(),
        Port = slave.Endpoint.Port,
        Format = rtu ? ModbusFrameFormat.RtuOverTcp : ModbusFrameFormat.Tcp,
        ReadTimeoutMs = 500,
        RtuSilenceMs = 100
    };

    private static OutboundConnector Connector() => new(new LoopbackPolicy());
    private sealed class LoopbackPolicy : IOutboundAddressPolicy
    {
        public bool IsAllowed(IPAddress address, int port) => IPAddress.IsLoopback(address);
    }
}
