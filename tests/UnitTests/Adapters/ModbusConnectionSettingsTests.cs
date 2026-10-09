using Microsoft.Extensions.Time.Testing;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Adapters.Modbus;

namespace TenonEdge.UnitTests.Adapters;

public sealed class ModbusConnectionSettingsTests
{
    private static ModbusConnectionSettings Valid => new() { ConnectionId = 1, Host = "DEVICE.local" };

    [Fact]
    public void IdentityNormalizesHostAndExcludesLogicalId()
    {
        var settings = Valid.Validate();
        Assert.Equal("device.local", settings.Host);
        Assert.Equal(settings.Identity, (settings with { ConnectionId = 2 }).Identity);
        Assert.NotEqual(settings.Identity, (settings with { ReadTimeoutMs = 2000 }).Identity);
    }

    [Theory]
    [InlineData(0, "device", 502, 1000)]
    [InlineData(9007199254740992L, "device", 502, 1000)]
    [InlineData(1, " device", 502, 1000)]
    [InlineData(1, "https://device", 502, 1000)]
    [InlineData(1, "device", 0, 1000)]
    [InlineData(1, "device", 65536, 1000)]
    [InlineData(1, "device", 502, 99)]
    [InlineData(1, "device", 502, 60001)]
    public void RejectsInvalidSettings(long id, string host, int port, int timeout) =>
        Assert.Throws<ArgumentException>(() => (Valid with { ConnectionId = id, Host = host, Port = port, ReadTimeoutMs = timeout }).Validate());

    [Fact]
    public async Task ConnectDeadlineBacksOffWithoutIssuingARead()
    {
        var time = new FakeTimeProvider();
        var connector = new WaitingConnector();
        await using var session = new ModbusConnectionSession(Valid, [1], connector, time);
        var read = session.ReadAsync(1, 3, 0, 1, TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromSeconds(5));
        var result = await read.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.False(result.WasRequested);
        Assert.Equal(ModbusRequestOutcome.ConnectionError, result.Outcome);
        Assert.False((await session.ReadAsync(1, 3, 0, 1, TestContext.Current.CancellationToken)).WasRequested);
        Assert.Equal(1, connector.Calls);
    }

    [Fact]
    public async Task ConnectCancellationPropagatesWithoutBackoff()
    {
        var connector = new WaitingConnector();
        await using var session = new ModbusConnectionSession(Valid, [1], connector, TimeProvider.System);
        for (var i = 0; i < 2; i++)
        {
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            var read = session.ReadAsync(1, 3, 0, 1, stop.Token);
            await stop.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        }
        Assert.Equal(2, connector.Calls);
    }

    [Fact]
    public void RejectsEmptyOrInvalidRtuUnitsBeforeConnecting()
    {
        var connector = new WaitingConnector();
        Assert.Throws<ArgumentException>(() => new ModbusConnectionSession(Valid, [], connector, TimeProvider.System));
        Assert.Throws<ArgumentException>(() => new ModbusConnectionSession(Valid with { Format = ModbusFrameFormat.RtuOverTcp }, [0], connector, TimeProvider.System));
        Assert.Equal(0, connector.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StreamReturnedAfterCancellationIsDisposed(bool deadline)
    {
        var time = new FakeTimeProvider();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var stream = new MemoryStream();
        var connector = new ReturningConnector(stream, () =>
        {
            if (deadline)
            { time.Advance(TimeSpan.FromSeconds(5)); }
            else
            { stop.Cancel(); }
        });
        await using var session = new ModbusConnectionSession(Valid, [1], connector, time);
        if (deadline)
        { Assert.False((await session.ReadAsync(1, 3, 0, 1, stop.Token)).WasRequested); }
        else
        { await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.ReadAsync(1, 3, 0, 1, stop.Token)); }
        Assert.False(stream.CanRead);
    }

    private sealed class ReturningConnector(Stream stream, Action beforeReturn) : IOutboundConnector
    {
        public ValueTask<Stream> ConnectAsync(string host, int port, CancellationToken cancellationToken)
        {
            beforeReturn();
            return ValueTask.FromResult(stream);
        }
    }

    private sealed class WaitingConnector : IOutboundConnector
    {
        internal int Calls { get; private set; }
        public async ValueTask<Stream> ConnectAsync(string host, int port, CancellationToken cancellationToken)
        {
            Calls++;
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable");
        }
    }
}
