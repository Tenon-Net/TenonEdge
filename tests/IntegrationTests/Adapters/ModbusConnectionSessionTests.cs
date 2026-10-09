using System.Net;
using Microsoft.Extensions.Time.Testing;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Adapters.Modbus;
using TenonEdge.IntegrationTests.Probes.Modbus;
using TenonEdge.Runtime.Connections;

namespace TenonEdge.IntegrationTests.Adapters;

public sealed class ModbusConnectionSessionTests
{
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
