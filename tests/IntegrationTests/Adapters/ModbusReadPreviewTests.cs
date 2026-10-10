using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Adapters.Modbus;
using TenonEdge.IntegrationTests.Probes.Modbus;
using TenonEdge.Runtime.Connections;

namespace TenonEdge.IntegrationTests.Adapters;

public sealed class ModbusReadPreviewTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealReadReturnsRawWordsFourOrdersAndExactLargeIntegers(bool rtu)
    {
        await using var slave = ModbusTestSlave.Start(rtu ? ModbusFraming.RtuOverTcp : ModbusFraming.Tcp);
        var preview = new ModbusReadPreview(new SharedConnections(), Connector(), TimeProvider.System);
        var result = await preview.RunAsync(Settings(slave, rtu), 1,
            [Point("large", 0, "uint64", "ABCD"), Point("flag", 0, "bit") with { Bit = 12 },
             new() { Key = "coil", FunctionCode = 1, Address = 2, Type = "bool" },
             Point("disabled", 100) with { Enabled = false }], Token);
        Assert.False(result.Truncated);
        Assert.False(result.DeadlineExceeded);
        Assert.Equal(3, result.TotalPoints);
        var large = result.Points.Single(point => point.Key == "large");
        Assert.Equal(["1000", "1001", "1002", "1003"], large.RawHex);
        Assert.Equal("400001", large.ManualReference);
        Assert.Equal("integer", large.Value.Type);
        Assert.Equal(0x1000100110021003UL.ToString(CultureInfo.InvariantCulture), large.Value.Text);
        Assert.True(large.Value.UnsafeJavaScriptInteger);
        Assert.Equal(4, large.Orders.Length);
        Assert.Equal(0x1003100210011000UL.ToString(CultureInfo.InvariantCulture), large.Orders[1].Value.Text);
        Assert.Equal(0x0010011002100310UL.ToString(CultureInfo.InvariantCulture), large.Orders[2].Value.Text);
        Assert.Equal(0x0310021001100010UL.ToString(CultureInfo.InvariantCulture), large.Orders[3].Value.Text);
        var bit = result.Points.Single(point => point.Key == "flag");
        Assert.Equal("0001000000000000", bit.Binary);
        Assert.Equal("true", bit.Value.Text);
        Assert.Equal("true", result.Points.Single(point => point.Key == "coil").Value.Text);
        Assert.All(result.Blocks, block => { Assert.True(block.Requested); Assert.NotNull(block.ReadAt); });
        Assert.Equal(2, slave.RequestCount);
        Assert.Equal(1, slave.ConnectionCount);
        var json = JsonSerializer.Serialize(result, Json);
        Assert.Contains("\"text\":\"" + large.Value.Text + "\"", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreviewOfDraftUnitBorrowsRuntimeStreamWithoutAddingHealthMember(bool rtu)
    {
        await using var slave = ModbusTestSlave.Start(rtu ? ModbusFraming.RtuOverTcp : ModbusFraming.Tcp,
            request => new SlaveReply { Silent = request.Unit == 2 });
        var settings = Settings(slave, rtu);
        var shared = new SharedConnections();
        await using var lease = shared.Acquire(1, "modbus", settings.Identity,
            () => new ModbusConnectionSession(settings, [1], Connector(), TimeProvider.System));
        Assert.Equal(ModbusRequestOutcome.Success, (await lease.ExecuteAsync((session, token) => session.ReadAsync(1, 3, 0, 1, token), Token)).Outcome);
        var preview = new ModbusReadPreview(shared, new ForbiddenConnector(), TimeProvider.System);
        var result = await preview.RunAsync(settings, 2, [Point("a", 0), Point("b", 10)], Token);
        Assert.Equal("readTimeout", result.Blocks[0].Outcome);
        Assert.True(result.Blocks[0].Requested);
        Assert.False(result.Blocks[1].Requested);
        Assert.All(result.Points, point => { Assert.Null(point.Value.Text); Assert.Empty(point.RawHex); });
        Assert.Equal(ModbusRequestOutcome.Success, (await lease.ExecuteAsync((session, token) => session.ReadAsync(1, 3, 0, 1, token), Token)).Outcome);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => lease.ExecuteAsync((session, token) => session.ReadAsync(2, 3, 0, 1, token), Token));
        Assert.Equal(1, slave.ConnectionCount);
        Assert.Equal(3, slave.RequestCount);
    }

    [Fact]
    public async Task DeviceExceptionRetainsExactBlockAndContinuesOtherConfiguredBlocks()
    {
        await using var slave = ModbusTestSlave.Start(ModbusFraming.Tcp,
            request => request.Address == 10 ? new SlaveReply { ExceptionCode = 2 } : SlaveReply.Normal);
        var preview = new ModbusReadPreview(new SharedConnections(), Connector(), TimeProvider.System);
        var result = await preview.RunAsync(Settings(slave), 255, [Point("bad", 10), Point("good", 20)], Token);
        Assert.Equal((byte)2, result.Blocks[0].ExceptionCode);
        Assert.Equal("deviceException", result.Points[0].Value.Reason);
        Assert.Null(result.Points[0].Value.Text);
        Assert.Equal("success", result.Blocks[1].Outcome);
        Assert.Equal("4116", result.Points[1].Value.Text);
        Assert.Equal(2, slave.RequestCount);
    }

    [Fact]
    public async Task DisplayBudgetCountsEscapedUtf8AndStopsFurtherNetworkReads()
    {
        await using var slave = ModbusTestSlave.Start(ModbusFraming.Tcp);
        var points = Enumerable.Range(0, 10000).Select(index => Point("p" + index, index * 6, "uint64", "ABCD")).ToImmutableArray();
        var preview = new ModbusReadPreview(new SharedConnections(), Connector(), TimeProvider.System);
        var result = await preview.RunAsync(Settings(slave), 0, points, Token);
        Assert.True(result.Truncated);
        Assert.Equal(10000, result.TotalPoints);
        Assert.NotEmpty(result.Points);
        Assert.True(result.Points.Length < 10000);
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(result, Json).Length <= ModbusReadPreview.MaximumDisplayBytes);
        Assert.Equal(result.Blocks.Length, slave.RequestCount);
    }

    [Fact]
    public async Task DeadlineOnActiveReadClosesAndJoinsIoWithoutReturningInventedValues()
    {
        await using var slave = ModbusTestSlave.Start(ModbusFraming.Tcp, _ => new SlaveReply { Silent = true });
        var time = new FakeTimeProvider();
        var preview = new ModbusReadPreview(new SharedConnections(), Connector(), time);
        var pending = preview.RunAsync(Settings(slave) with { ReadTimeoutMs = 30000 }, 1, [Point("a", 0), Point("b", 10)], Token, timeoutMs: 1000);
        await slave.WaitForRequestsAsync(1, TimeSpan.FromSeconds(5), Token);
        time.Advance(TimeSpan.FromSeconds(1));
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.True(result.DeadlineExceeded);
        Assert.All(result.Points, point => Assert.Null(point.Value.Text));
        Assert.All(result.Blocks, block => Assert.Equal("deadline", block.Outcome));
        Assert.Equal(1, slave.RequestCount);
    }

    [Fact]
    public async Task CallerCancellationPropagatesAndReleasesFinalLease()
    {
        await using var slave = ModbusTestSlave.Start(ModbusFraming.Tcp, _ => new SlaveReply { Silent = true });
        var shared = new SharedConnections();
        var settings = Settings(slave) with { ReadTimeoutMs = 30000 };
        var preview = new ModbusReadPreview(shared, Connector(), TimeProvider.System);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var pending = preview.RunAsync(settings, 1, [Point("a", 0)], stop.Token);
        await slave.WaitForRequestsAsync(1, TimeSpan.FromSeconds(5), Token);
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5), Token));
        await using var next = shared.Acquire(1, "modbus", settings.Identity,
            () => new ModbusConnectionSession(settings, [1], Connector(), TimeProvider.System));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(248)]
    public async Task InvalidRtuUnitFailsBeforeNetwork(byte unit)
    {
        await using var slave = ModbusTestSlave.Start(ModbusFraming.RtuOverTcp);
        var preview = new ModbusReadPreview(new SharedConnections(), new ForbiddenConnector(), TimeProvider.System);
        await Assert.ThrowsAsync<ArgumentException>(() => preview.RunAsync(Settings(slave, true), unit, [Point("a", 0)], Token));
        Assert.Equal(0, slave.ConnectionCount);
    }

    [Fact]
    public async Task NonFiniteValueKeepsRawWordsAndAlternativeOrderDiagnostics()
    {
        await using var slave = ModbusTestSlave.Start(ModbusFraming.Tcp);
        var preview = new ModbusReadPreview(new SharedConnections(), Connector(), TimeProvider.System);
        var result = await preview.RunAsync(Settings(slave), 1, [Point("nan", 0x6f80, "float32", "ABCD")], Token);
        var point = Assert.Single(result.Points);
        Assert.Equal(["7F80", "7F81"], point.RawHex);
        Assert.Equal("nonFinite", point.Value.Reason);
        Assert.Null(point.Value.Text);
        Assert.Equal(4, point.Orders.Length);
        Assert.Contains(point.Orders, order => order.Value.Quality == "good");
    }

    [Fact]
    public async Task QueuedDeadlineDoesNotAbortAnotherRuntimeUser()
    {
        await using var slave = ModbusTestSlave.Start(ModbusFraming.Tcp);
        var settings = Settings(slave);
        var shared = new SharedConnections();
        await using var lease = shared.Acquire(1, "modbus", settings.Identity,
            () => new ModbusConnectionSession(settings, [1], Connector(), TimeProvider.System));
        Assert.Equal(ModbusRequestOutcome.Success, (await lease.ExecuteAsync((session, token) => session.ReadAsync(1, 3, 0, 1, token), Token)).Outcome);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = lease.ExecuteAsync(async (_, token) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return true;
        }, Token);
        await entered.Task.WaitAsync(Token);
        var time = new FakeTimeProvider();
        try
        {
            var preview = new ModbusReadPreview(shared, new ForbiddenConnector(), time);
            var pending = preview.RunAsync(settings, 1, [Point("a", 0)], Token, timeoutMs: 1000);
            time.Advance(TimeSpan.FromSeconds(1));
            var result = await pending.WaitAsync(TimeSpan.FromSeconds(5), Token);
            Assert.True(result.DeadlineExceeded);
            Assert.Null(Assert.Single(result.Points).Value.Text);
            Assert.Equal(1, slave.RequestCount);
        }
        finally { release.TrySetResult(); }
        await active;
        Assert.Equal(ModbusRequestOutcome.Success, (await lease.ExecuteAsync((session, token) => session.ReadAsync(1, 3, 0, 1, token), Token)).Outcome);
        Assert.Equal(1, slave.ConnectionCount);
    }

    [Fact]
    public async Task MultipleGroupsShareOneDisplayBudgetAndKeepBoundaries()
    {
        await using var slave = ModbusTestSlave.Start(ModbusFraming.Tcp);
        var preview = new ModbusReadPreview(new SharedConnections(), Connector(), TimeProvider.System);
        var points = Enumerable.Range(0, 100).Select(index => Point("p" + index, index * 6, "uint64", "ABCD")).ToImmutableArray();
        var result = await preview.RunGroupsAsync(Settings(slave), 1,
            [new("a", points), new("b", points), new("c", points)], Token);
        Assert.True(result.Truncated);
        Assert.Equal(3, result.TotalGroups);
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(result, Json).Length <= ModbusReadPreview.MaximumDisplayBytes);
        Assert.True(result.Groups.Length < 3);
        Assert.Equal(1, slave.ConnectionCount);
    }

    [Fact]
    public async Task MultipleGroupsShareDeadlineAndPreserveCompletedFirstGroup()
    {
        await using var slave = ModbusTestSlave.Start(ModbusFraming.Tcp, request => new SlaveReply { Silent = request.Sequence == 2 });
        var time = new FakeTimeProvider();
        var preview = new ModbusReadPreview(new SharedConnections(), Connector(), time);
        var pending = preview.RunGroupsAsync(Settings(slave) with { ReadTimeoutMs = 30000 }, 1,
            [new("a", [Point("one", 0)]), new("b", [Point("two", 10)]), new("c", [Point("three", 20)])], Token, timeoutMs: 1000);
        await slave.WaitForRequestsAsync(2, TimeSpan.FromSeconds(5), Token);
        time.Advance(TimeSpan.FromSeconds(1));
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.True(result.DeadlineExceeded);
        Assert.Equal(3, result.TotalGroups);
        Assert.Equal(2, result.Groups.Length);
        Assert.Equal("4096", Assert.Single(result.Groups[0].Preview.Points).Value.Text);
        Assert.Null(Assert.Single(result.Groups[1].Preview.Points).Value.Text);
        Assert.Equal(2, slave.RequestCount);
        Assert.Equal(1, slave.ConnectionCount);
    }

    [Fact]
    public async Task InvalidLaterGroupIsValidatedBeforeFirstNetworkRequest()
    {
        await using var slave = ModbusTestSlave.Start(ModbusFraming.Tcp);
        var preview = new ModbusReadPreview(new SharedConnections(), new ForbiddenConnector(), TimeProvider.System);
        await Assert.ThrowsAnyAsync<ArgumentException>(() => preview.RunGroupsAsync(Settings(slave), 1,
            [new("a", [Point("one", 0)]), new("b", [Point("two", 65535, "uint64", "ABCD")])], Token));
        Assert.Equal(0, slave.ConnectionCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RouteSampleUsesFullTypedMessageAndBorrowsRuntimeConnectionWithoutChangingMembers(bool rtu)
    {
        await using var slave = ModbusTestSlave.Start(rtu ? ModbusFraming.RtuOverTcp : ModbusFraming.Tcp);
        var settings = Settings(slave, rtu);
        var shared = new SharedConnections();
        await using var lease = shared.Acquire(1, "modbus", settings.Identity,
            () => new ModbusConnectionSession(settings, [1], Connector(), TimeProvider.System));
        await lease.ExecuteAsync((session, token) => session.ReadAsync(1, 3, 0, 1, token), Token);
        var preview = new ModbusReadPreview(shared, new ForbiddenConnector(), TimeProvider.System);
        var result = await preview.ReadSampleAsync(SampleSettings(slave, rtu) with { UnitId = 2 },
            "input", "device", ClockSyncState.NotSynchronized, Token);
        Assert.Equal("success", result.Code);
        var message = Assert.IsType<EdgeMessage>(result.Message);
        Assert.Null(message.RawJson);
        Assert.Equal(TimeSource.GatewayUnsynced, message.TimeSource);
        Assert.Equal(0x1000100110021003UL.ToString(CultureInfo.InvariantCulture), message.Values["value"].ToString());
        Assert.Equal(message.ReceivedAt, message.CollectedAt);
        Assert.Equal(1, slave.ConnectionCount);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => lease.ExecuteAsync((session, token) =>
            session.ReadAsync(2, 3, 0, 1, token), Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RouteSamplePreservesFailedFieldQualityOrRejectsEntirelyFailedCycle(bool allFail)
    {
        await using var slave = ModbusTestSlave.Start(ModbusFraming.Tcp,
            request => allFail || request.Address == 10 ? new SlaveReply { ExceptionCode = 2 } : SlaveReply.Normal);
        var preview = new ModbusReadPreview(new SharedConnections(), Connector(), TimeProvider.System);
        var settings = SampleSettings(slave) with { Points = [Point("good", 0), Point("bad", 10)] };
        var result = await preview.ReadSampleAsync(settings, "input", "device", ClockSyncState.Unknown, Token);
        Assert.Equal(2, slave.RequestCount);
        if (allFail)
        {
            Assert.Equal("readFailed", result.Code);
            Assert.Null(result.Message);
        }
        else
        {
            var message = Assert.IsType<EdgeMessage>(result.Message);
            Assert.Equal("4096", message.Values["good"].ToString());
            Assert.False(message.Values.ContainsKey("bad"));
            Assert.Equal(QualityCode.DeviceException, message.Quality["bad"]);
        }
    }

    [Fact]
    public async Task RouteSampleDoesNotStopAtTheDiagnosticDisplayBudget()
    {
        await using var slave = ModbusTestSlave.Start(ModbusFraming.Tcp);
        var preview = new ModbusReadPreview(new SharedConnections(), Connector(), TimeProvider.System);
        var settings = SampleSettings(slave) with { Points = Enumerable.Range(0, 1000).Select(i => Point("p" + i, i)).ToImmutableArray() };
        var result = await preview.ReadSampleAsync(settings, "input", "device", ClockSyncState.Unknown, Token);
        Assert.Equal(1000, result.Message!.Values.Count);
        Assert.Equal("5095", result.Message.Values["p999"].ToString());
        Assert.Equal(8, slave.RequestCount);
    }

    [Fact]
    public async Task RouteSampleDeadlineDiscardsPartialCycle()
    {
        await using var slave = ModbusTestSlave.Start(ModbusFraming.Tcp, request => new SlaveReply { Silent = request.Sequence == 2 });
        var time = new FakeTimeProvider();
        var preview = new ModbusReadPreview(new SharedConnections(), Connector(), time);
        var pending = preview.ReadSampleAsync(SampleSettings(slave) with { ReadTimeoutMs = 30000, Points = [Point("a", 0), Point("b", 10)] },
            "input", "device", ClockSyncState.Unknown, Token, timeoutMs: 1000);
        await slave.WaitForRequestsAsync(2, TimeSpan.FromSeconds(5), Token);
        time.Advance(TimeSpan.FromSeconds(1));
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.Equal("deadline", result.Code);
        Assert.Null(result.Message);
    }

    [Fact]
    public async Task RouteSampleCancellationWaitsForReadExitAndAllowsNextSample()
    {
        var silent = true;
        await using var slave = ModbusTestSlave.Start(ModbusFraming.Tcp, _ => new SlaveReply { Silent = Volatile.Read(ref silent) });
        var preview = new ModbusReadPreview(new SharedConnections(), Connector(), TimeProvider.System);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var pending = preview.ReadSampleAsync(SampleSettings(slave) with { ReadTimeoutMs = 30000 },
            "input", "device", ClockSyncState.Unknown, stop.Token);
        await slave.WaitForRequestsAsync(1, TimeSpan.FromSeconds(5), Token);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5), Token));
        Volatile.Write(ref silent, false);
        var next = await preview.ReadSampleAsync(SampleSettings(slave), "input", "device", ClockSyncState.Unknown, Token);
        Assert.Equal("success", next.Code);
    }

    [Fact]
    public async Task RouteSampleKeepsNonFiniteAsBadQualityRatherThanFabricatingZero()
    {
        await using var slave = ModbusTestSlave.Start(ModbusFraming.Tcp);
        var preview = new ModbusReadPreview(new SharedConnections(), Connector(), TimeProvider.System);
        var result = await preview.ReadSampleAsync(SampleSettings(slave) with { Points = [Point("nan", 0x6ff0, "float64", "ABCD")] },
            "input", "device", ClockSyncState.Unknown, Token);
        Assert.Equal("success", result.Code);
        Assert.Empty(result.Message!.Values);
        Assert.Equal(QualityCode.NonFinite, result.Message.Quality["nan"]);
    }

    private static ModbusInputSettings SampleSettings(ModbusTestSlave slave, bool rtu = false) => new()
    {
        ConnectionId = 1,
        Host = "127.0.0.1",
        Port = slave.Endpoint.Port,
        FrameFormat = rtu ? "rtu-over-tcp" : "tcp",
        ReadTimeoutMs = 500,
        RtuSilenceMs = 100,
        UnitId = 1,
        Points = [Point("value", 0, "uint64", "ABCD")]
    };

    private static ModbusPointSettings Point(string key, int address, string type = "uint16", string? order = null) =>
        new() { Key = key, FunctionCode = 3, Address = address, Type = type, ByteOrder = order };
    private static ModbusConnectionSettings Settings(ModbusTestSlave slave, bool rtu = false) => new()
    {
        ConnectionId = 1,
        Host = "127.0.0.1",
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
    private sealed class ForbiddenConnector : IOutboundConnector
    {
        public ValueTask<Stream> ConnectAsync(string host, int port, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Preview must borrow the existing runtime connector");
    }
}
