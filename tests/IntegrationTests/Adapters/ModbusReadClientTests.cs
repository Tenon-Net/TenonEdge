using System.Net;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Adapters.Modbus;
using TenonEdge.IntegrationTests.Probes.Modbus;
using TenonEdge.Runtime.Connections;

namespace TenonEdge.IntegrationTests.Adapters;

public sealed class ModbusReadClientTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static ModbusFrameFormat Format(bool rtu) => rtu ? ModbusFrameFormat.RtuOverTcp : ModbusFrameFormat.Tcp;
    private static ModbusFraming Framing(bool rtu) => rtu ? ModbusFraming.RtuOverTcp : ModbusFraming.Tcp;
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(2);

    [Theory]
    [InlineData(false, 0, 3)]
    [InlineData(false, 255, 4)]
    [InlineData(true, 1, 3)]
    [InlineData(true, 247, 4)]
    public async Task ReadsKnownRegistersThroughCheckedOutboundStream(bool rtu, byte unit, byte function)
    {
        await using var slave = ModbusTestSlave.Start(Framing(rtu));
        using var client = new ModbusReadClient(await ConnectAsync(slave), Format(rtu), TimeProvider.System);
        await using var executor = new ModbusRequestExecutor();
        var values = await executor.ExecuteAsync(() => client.ReadRegisters(unit, function, 5, 3, ReadTimeout), client.Abort, Token);
        Assert.Equal("4101,4102,4103", string.Join(',', values));
        var request = Assert.Single(slave.Requests);
        Assert.Equal((unit, function, (ushort)5, (ushort)3), (request.Unit, request.FunctionCode, request.Address, request.Quantity));
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    public async Task ReadsKnownBitsAcrossAByteBoundary(bool rtu, byte function)
    {
        await using var slave = ModbusTestSlave.Start(Framing(rtu));
        using var client = new ModbusReadClient(await ConnectAsync(slave), Format(rtu), TimeProvider.System);
        await using var executor = new ModbusRequestExecutor();
        var bits = await executor.ExecuteAsync(() => client.ReadBits(1, function, 5, 9, ReadTimeout), client.Abort, Token);
        Assert.Equal("False,True,False,True,False,True,False,True,False", string.Join(',', bits));
        Assert.Single(slave.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SilentSlaveTimesOutWithoutLibraryResending(bool rtu)
    {
        await using var slave = ModbusTestSlave.Start(Framing(rtu), _ => new SlaveReply { Silent = true });
        using var client = new ModbusReadClient(await ConnectAsync(slave), Format(rtu), TimeProvider.System);
        await using var executor = new ModbusRequestExecutor();
        var observed = Record.ExceptionAsync(() =>
            executor.ExecuteAsync(() => client.ReadRegisters(1, 3, 0, 1, TimeSpan.FromMilliseconds(300)), client.Abort, Token));
        var error = await observed.AsTask().WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.True(error is IOException or TimeoutException, $"unexpected: {error}");
        Assert.Single(slave.Requests);
    }

    [Theory]
    [InlineData(false, 2)]
    [InlineData(false, 5)]
    [InlineData(false, 6)]
    [InlineData(true, 2)]
    [InlineData(true, 5)]
    [InlineData(true, 6)]
    public async Task DeviceExceptionsIncludingAckAndBusyFinishWithoutRetry(bool rtu, byte code)
    {
        await using var slave = ModbusTestSlave.Start(Framing(rtu), _ => new SlaveReply { ExceptionCode = code });
        using var client = new ModbusReadClient(await ConnectAsync(slave), Format(rtu), TimeProvider.System);
        await using var executor = new ModbusRequestExecutor();
        var error = await Assert.ThrowsAsync<ModbusDeviceException>(() =>
            executor.ExecuteAsync(() => client.ReadRegisters(1, 3, 0, 1, ReadTimeout), client.Abort, Token));
        Assert.Equal(code, error.Code);
        Assert.Single(slave.Requests);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task WrongUnitIsNotAcceptedEvenForAnExceptionResponse(bool rtu, bool exception)
    {
        await using var slave = ModbusTestSlave.Start(Framing(rtu), _ => new SlaveReply { Unit = 2, ExceptionCode = exception ? (byte)5 : null });
        using var client = new ModbusReadClient(await ConnectAsync(slave), Format(rtu), TimeProvider.System);
        await using var executor = new ModbusRequestExecutor();
        await Assert.ThrowsAsync<IOException>(() =>
            executor.ExecuteAsync(() => client.ReadRegisters(1, 3, 0, 1, ReadTimeout), client.Abort, Token));
        Assert.Single(slave.Requests);
    }

    [Fact]
    public async Task RtuCrcErrorIsRejected()
    {
        await using var slave = ModbusTestSlave.Start(ModbusFraming.RtuOverTcp, _ => new SlaveReply { CorruptCrc = true });
        using var client = new ModbusReadClient(await ConnectAsync(slave), ModbusFrameFormat.RtuOverTcp, TimeProvider.System);
        await using var executor = new ModbusRequestExecutor();
        await Assert.ThrowsAsync<IOException>(() =>
            executor.ExecuteAsync(() => client.ReadRegisters(1, 3, 0, 1, ReadTimeout), client.Abort, Token));
    }

    [Fact]
    public async Task OlderTcpFrameIsSkippedWithoutResendingOrConfusingDifferentFunction()
    {
        await using var slave = ModbusTestSlave.Start(ModbusFraming.Tcp, request => new SlaveReply { Duplicate = request.Sequence == 1 });
        using var client = new ModbusReadClient(await ConnectAsync(slave), ModbusFrameFormat.Tcp, TimeProvider.System);
        await using var executor = new ModbusRequestExecutor();
        Assert.Equal((ushort)4096, Assert.Single(await executor.ExecuteAsync(() => client.ReadRegisters(1, 3, 0, 1, ReadTimeout), client.Abort, Token)));
        Assert.Equal((ushort)4103, Assert.Single(await executor.ExecuteAsync(() => client.ReadRegisters(2, 4, 7, 1, ReadTimeout), client.Abort, Token)));
        Assert.Equal(2, slave.Requests.Count);
        Assert.Equal("1,2", string.Join(',', slave.Requests.Select(request => request.TransactionId)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationClosesCapturedConnectionAndJoinsRealBlockedRead(bool rtu)
    {
        await using var slave = ModbusTestSlave.Start(Framing(rtu), _ => new SlaveReply { Silent = true });
        using var client = new ModbusReadClient(await ConnectAsync(slave), Format(rtu), TimeProvider.System);
        await using var executor = new ModbusRequestExecutor();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var read = executor.ExecuteAsync(() => client.ReadRegisters(1, 3, 0, 1, TimeSpan.FromSeconds(30)), client.Abort, stop.Token);
        await slave.WaitForRequestsAsync(1, TimeSpan.FromSeconds(5), Token);
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read.WaitAsync(TimeSpan.FromSeconds(3), Token));
        Assert.Equal(42, await executor.ExecuteAsync(() => 42, () => { }, Token));
        Assert.Single(slave.Requests);
    }

    private static ValueTask<Stream> ConnectAsync(ModbusTestSlave slave) =>
        new OutboundConnector(new LoopbackPolicy()).ConnectAsync(slave.Endpoint.Address.ToString(), slave.Endpoint.Port, Token);

    private sealed class LoopbackPolicy : IOutboundAddressPolicy
    {
        public bool IsAllowed(IPAddress address, int port) => IPAddress.IsLoopback(address);
    }
}
