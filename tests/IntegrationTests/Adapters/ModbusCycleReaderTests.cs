using System.Collections.Immutable;
using System.Net;
using Microsoft.Extensions.Time.Testing;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Adapters.Modbus;
using TenonEdge.IntegrationTests.Probes.Modbus;
using TenonEdge.Runtime.Connections;

namespace TenonEdge.IntegrationTests.Adapters;

public sealed class ModbusCycleReaderTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly ImmutableArray<ModbusReadBlock> Blocks = ModbusReadPlanner.Build(
        [new(ModbusAddress.FromOffset(3, 0, 1), 1), new(ModbusAddress.FromOffset(3, 10, 1), 1), new(ModbusAddress.FromOffset(4, 20, 1), 1)]);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FirstTimeoutFastFailsRemainingBlocksButNotAnotherUnit(bool rtu)
    {
        await using var slave = ModbusTestSlave.Start(rtu ? ModbusFraming.RtuOverTcp : ModbusFraming.Tcp,
            request => new SlaveReply { Silent = request.Unit == 1 });
        await using var lease = Acquire(slave, rtu);
        var failed = await Cycle(lease, 1);
        Assert.False(failed.Overrun);
        Assert.True(failed.HasActualRequest);
        Assert.False(failed.HasSuccessfulRead);
        Assert.All(failed.Blocks, block => Assert.Equal(ModbusRequestOutcome.Timeout, block.Outcome));
        Assert.Equal("True,False,False", string.Join(',', failed.Blocks.Select(block => block.WasRequested)));
        Assert.Single(slave.Requests);
        var healthy = await Cycle(lease, 2);
        Assert.True(healthy.HasSuccessfulRead);
        Assert.Equal(new ushort[] { 4096, 4106, 4116 }, healthy.Blocks.Select(block => Assert.Single(block.Registers!)));
        Assert.Equal(4, slave.RequestCount);
        Assert.Equal(1, slave.ConnectionCount);
    }

    [Fact]
    public async Task PartialSuccessPreservesOnlyCurrentResultsAndDeviceExceptionDoesNotFastFail()
    {
        await using var slave = ModbusTestSlave.Start(ModbusFraming.Tcp,
            request => request.Sequence == 2 ? new SlaveReply { ExceptionCode = 2 } : SlaveReply.Normal);
        await using var lease = Acquire(slave);
        var cycle = await Cycle(lease, 1);
        Assert.True(cycle.HasSuccessfulRead);
        Assert.Equal(new[] { ModbusRequestOutcome.Success, ModbusRequestOutcome.DeviceException, ModbusRequestOutcome.Success },
            cycle.Blocks.Select(block => block.Outcome));
        Assert.Null(cycle.Blocks[1].Registers);
        Assert.Equal((byte)2, cycle.Blocks[1].ExceptionCode);
        var next = await Cycle(lease, 1);
        Assert.All(next.Blocks, block => Assert.Equal(ModbusRequestOutcome.Success, block.Outcome));
        Assert.Null(next.Blocks[1].ExceptionCode);
        Assert.Equal(6, slave.RequestCount);
    }

    [Fact]
    public async Task DeadlineDiscardsEarlierSuccessAndJoinsActualReadBeforeReturningOverrun()
    {
        await using var slave = ModbusTestSlave.Start(ModbusFraming.Tcp,
            request => new SlaveReply { Silent = request.Sequence == 2 });
        var time = new FakeTimeProvider();
        await using var lease = Acquire(slave, time: time, timeout: 30000);
        var pending = ModbusCycleReader.ReadAsync(lease, 1, Blocks, TimeSpan.FromSeconds(1), time, Token);
        await slave.WaitForRequestsAsync(2, TimeSpan.FromSeconds(5), Token);
        time.Advance(TimeSpan.FromSeconds(1));
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.True(result.Overrun);
        Assert.Empty(result.Blocks);
        Assert.False(result.HasSuccessfulRead);
        // 已退出被打断的工作项；下一周期可连接新一代，不能返回已读出的第一块为新采样。
        Assert.True((await Cycle(lease, 2)).HasSuccessfulRead);
        Assert.Equal(2, slave.ConnectionCount);
        Assert.Equal(5, slave.RequestCount);
    }

    [Fact]
    public async Task StopCancellationIsNotReportedAsOverrun()
    {
        await using var slave = ModbusTestSlave.Start(ModbusFraming.Tcp, _ => new SlaveReply { Silent = true });
        await using var lease = Acquire(slave, timeout: 30000);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var pending = ModbusCycleReader.ReadAsync(lease, 1, Blocks, TimeSpan.FromSeconds(60), TimeProvider.System, stop.Token);
        await slave.WaitForRequestsAsync(1, TimeSpan.FromSeconds(5), Token);
        await stop.CancelAsync();
        var observed = Record.ExceptionAsync(() => pending);
        Assert.IsAssignableFrom<OperationCanceledException>(await observed.AsTask().WaitAsync(TimeSpan.FromSeconds(5), Token));
        Assert.Single(slave.Requests);
    }

    [Fact]
    public async Task QueueDeadlineDoesNotCancelCurrentLeaseOperationOrSendAnything()
    {
        await using var slave = ModbusTestSlave.Start(ModbusFraming.Tcp);
        await using var lease = Acquire(slave);
        Assert.True((await Cycle(lease, 1)).HasSuccessfulRead);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holding = lease.ExecuteAsync(async (_, token) =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(token);
            return true;
        }, Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            var time = new FakeTimeProvider();
            var queued = ModbusCycleReader.ReadAsync(lease, 2, Blocks, TimeSpan.FromSeconds(1), time, Token);
            time.Advance(TimeSpan.FromSeconds(1));
            Assert.True((await queued.WaitAsync(TimeSpan.FromSeconds(5), Token)).Overrun);
            Assert.False(holding.IsCompleted);
            Assert.Equal(3, slave.RequestCount);
        }
        finally { release.TrySetResult(); }
        await holding;
        Assert.True((await Cycle(lease, 2)).HasSuccessfulRead);
        Assert.Equal(1, slave.ConnectionCount);
    }

    [Fact]
    public async Task ThreeRealFailedCyclesBackOffOnlySilentDeviceAndRecoveryResetsState()
    {
        var silent = 1;
        await using var slave = ModbusTestSlave.Start(ModbusFraming.Tcp,
            request => new SlaveReply { Silent = request.Unit == 1 && Volatile.Read(ref silent) != 0 });
        var time = new FakeTimeProvider();
        await using var lease = Acquire(slave, time: time);
        var state = new ModbusDevicePollingState(TimeSpan.FromSeconds(1), time);
        for (var cycle = 0; cycle < 3; cycle++)
        {
            Assert.True(state.CanRead);
            state.Record(await Cycle(lease, 1));
            Assert.True((await Cycle(lease, 2)).HasSuccessfulRead);
        }
        Assert.True(state.IsOffline);
        Assert.False(state.CanRead);
        Assert.Equal(12, slave.RequestCount);
        Assert.True((await Cycle(lease, 2)).HasSuccessfulRead);
        Assert.Equal(3, slave.Requests.Count(request => request.Unit == 1));
        time.Advance(TimeSpan.FromSeconds(2));
        Volatile.Write(ref silent, 0);
        Assert.True(state.CanRead);
        state.Record(await Cycle(lease, 1));
        Assert.False(state.IsOffline);
        Assert.True(state.CanRead);
        Assert.Equal(1, slave.ConnectionCount);
    }

    [Fact]
    public async Task ExpiredCycleDoesNotConnectAndCannotReturnPreviousValues()
    {
        await using var slave = ModbusTestSlave.Start(ModbusFraming.Tcp);
        await using var lease = Acquire(slave);
        var expired = await ModbusCycleReader.ReadAsync(lease, 1, Blocks, TimeSpan.Zero, TimeProvider.System, Token);
        Assert.True(expired.Overrun);
        Assert.Empty(expired.Blocks);
        Assert.Equal(0, slave.ConnectionCount);
        var success = await Cycle(lease, 1);
        Assert.True(success.HasSuccessfulRead);
        expired = await ModbusCycleReader.ReadAsync(lease, 1, Blocks, TimeSpan.FromSeconds(-1), TimeProvider.System, Token);
        Assert.True(expired.Overrun);
        Assert.Empty(expired.Blocks);
        Assert.Equal(3, slave.RequestCount);
    }

    [Fact]
    public async Task InvalidLaterBlockIsRejectedBeforeAnyNetworkIo()
    {
        await using var slave = ModbusTestSlave.Start(ModbusFraming.Tcp);
        await using var lease = Acquire(slave);
        var invalid = Blocks.SetItem(2, Blocks[2] with { FunctionCode = 5 });
        await Assert.ThrowsAsync<ArgumentException>(() => ModbusCycleReader.ReadAsync(lease, 1, invalid, TimeSpan.FromSeconds(1), TimeProvider.System, Token));
        Assert.Equal(0, slave.RequestCount);
        Assert.Equal(0, slave.ConnectionCount);
    }

    private static Task<ModbusCycleResult> Cycle(IConnectionLease<ModbusConnectionSession> lease, byte unit) =>
        ModbusCycleReader.ReadAsync(lease, unit, Blocks, TimeSpan.FromSeconds(10), TimeProvider.System, Token);

    private static IConnectionLease<ModbusConnectionSession> Acquire(ModbusTestSlave slave, bool rtu = false, TimeProvider? time = null, int timeout = 500)
    {
        var settings = new ModbusConnectionSettings
        {
            ConnectionId = 1,
            Host = slave.Endpoint.Address.ToString(),
            Port = slave.Endpoint.Port,
            Format = rtu ? ModbusFrameFormat.RtuOverTcp : ModbusFrameFormat.Tcp,
            ReadTimeoutMs = timeout,
            RtuSilenceMs = 100
        };
        return new SharedConnections().Acquire(1, "modbus", settings.Identity,
            () => new ModbusConnectionSession(settings, [1, 2], new OutboundConnector(new LoopbackPolicy()), time ?? TimeProvider.System));
    }

    private sealed class LoopbackPolicy : IOutboundAddressPolicy
    {
        public bool IsAllowed(IPAddress address, int port) => IPAddress.IsLoopback(address);
    }
}
