using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Abstractions.Plans;
using TenonEdge.Adapters.Modbus;
using TenonEdge.IntegrationTests.Infrastructure;
using TenonEdge.IntegrationTests.Probes.Modbus;
using TenonEdge.Runtime.Connections;
using TenonEdge.Runtime.Deliveries;
using TenonEdge.Runtime.Diagnostics;

namespace TenonEdge.IntegrationTests.Runtime;

public sealed class ModbusInputTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActivationGateThenRealPollingProducesBudgetedMessage(bool rtu)
    {
        await using var slave = ModbusTestSlave.Start(rtu ? ModbusFraming.RtuOverTcp : ModbusFraming.Tcp);
        await using var harness = new Harness();
        var input = harness.Add(Plan(slave, "device/fast", 1, rtu: rtu));
        harness.Start();
        Assert.Equal(0, slave.ConnectionCount);
        Assert.False(input.Channel.Reader.TryRead(out _));
        harness.Activate();
        var message = await input.Channel.Reader.ReadAsync(Token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.Equal(FieldValue.FromInt64(4096), message.Values["value"]);
        Assert.Equal(TimeSource.GatewayUnsynced, message.TimeSource);
        Assert.Equal(1, input.Reads.Snapshot().ReadsCompleted);
        Assert.Equal(9216, harness.Budget.Status.PeakReservedBytes);
        Assert.Equal(0, harness.Budget.Status.ReservedBytes);
        Assert.Single(slave.Requests);
        await harness.StopAsync();
        Assert.Equal(0, harness.Budget.Status.ReservedBytes);
    }

    [Fact]
    public async Task TwoUnitsShareOneConnectionAndSilentUnitDoesNotBlockHealthyOne()
    {
        await using var slave = ModbusTestSlave.Start(ModbusFraming.Tcp, request => new SlaveReply { Silent = request.Unit == 1 });
        await using var harness = new Harness();
        var silent = harness.Add(Plan(slave, "silent/fast", 1));
        var healthy = harness.Add(Plan(slave, "healthy/fast", 2));
        harness.Start();
        harness.Activate();
        var message = await healthy.Channel.Reader.ReadAsync(Token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.Equal(FieldValue.FromInt64(4096), message.Values["value"]);
        await OutputPipelineTests.UntilAsync(() => silent.Reads.Snapshot().ReadsFailed == 1);
        Assert.False(silent.Channel.Reader.TryRead(out _));
        Assert.Equal(1, slave.ConnectionCount);
        Assert.Equal(2, slave.RequestCount);
    }

    [Fact]
    public async Task NoBudgetMeansNoNetworkAndDeadlineCountsOneOverrun()
    {
        await using var slave = ModbusTestSlave.Start(ModbusFraming.Tcp);
        await using var harness = new Harness();
        Assert.True(harness.Budget.TryReserve(harness.Budget.Capacity));
        var input = harness.Add(Plan(slave, "device/fast", 1));
        harness.Activate();
        harness.Start();
        harness.Time.Advance(TimeSpan.FromSeconds(1));
        await OutputPipelineTests.UntilAsync(() => input.Reads.Snapshot().CyclesSkipped == 1);
        Assert.Equal(0, slave.ConnectionCount);
        Assert.Equal(0, input.Reads.Snapshot().ReadsFailed);
        await harness.StopAsync();
        harness.Budget.Release(harness.Budget.Capacity);
    }

    [Fact]
    public async Task StopDuringRealBlockedReadWaitsForExitAndReleasesBudget()
    {
        await using var slave = ModbusTestSlave.Start(ModbusFraming.Tcp, _ => new SlaveReply { Silent = true });
        await using var harness = new Harness();
        var input = harness.Add(Plan(slave, "device/fast", 1, timeout: 30000));
        harness.Start();
        harness.Activate();
        await slave.WaitForRequestsAsync(1, TimeSpan.FromSeconds(5), Token);
        await harness.StopAsync();
        Assert.Equal(0, harness.Budget.Status.ReservedBytes);
        Assert.Equal(0, input.Reads.Snapshot().CyclesSkipped);
        Assert.False(input.Channel.Reader.TryRead(out _));
    }

    [Fact]
    public async Task RejectsConflictingGroupsAndRebuildUsesSamePlanBinding()
    {
        await using var slave = ModbusTestSlave.Start(ModbusFraming.Tcp);
        await using var harness = new Harness();
        var firstPlan = Plan(slave, "device/fast", 1);
        var first = harness.Add(firstPlan);
        var rebuilt = harness.Factory.Build(firstPlan, first.Context);
        Assert.NotSame(first.Unit, rebuilt);
        await rebuilt.DisposeAsync();
        Assert.Throws<RuntimeUnitBuildException>(() => harness.Add(Plan(slave, "device/slow", 2, key: "other")));
        Assert.Throws<RuntimeUnitBuildException>(() => harness.Add(Plan(slave, "device/slow", 1)));
        var second = harness.Add(Plan(slave, "device/slow", 1, key: "other"));
        harness.Start();
        harness.Activate();
        await first.Channel.Reader.ReadAsync(Token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), Token);
        await second.Channel.Reader.ReadAsync(Token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.Equal(1, slave.ConnectionCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PhysicalUnitSharesFastFailureOfflineDiagnosticsAndRecoveryAcrossBindings(bool alias)
    {
        var silent = 1;
        await using var slave = ModbusTestSlave.Start(ModbusFraming.Tcp,
            request => new SlaveReply { Silent = request.Unit == 1 && Volatile.Read(ref silent) != 0 });
        await using var harness = new Harness();
        var first = harness.Add(Plan(slave, "silent/fast", 1));
        var second = harness.Add(Plan(slave, alias ? "alias/fast" : "silent/slow", 1, key: alias ? "value" : "other"));
        var healthy = harness.Add(Plan(slave, "healthy/fast", 2));
        harness.Activate();
        harness.Start();
        for (var cycle = 1; cycle <= 3; cycle++)
        {
            await healthy.Channel.Reader.ReadAsync(Token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), Token);
            await OutputPipelineTests.UntilAsync(() => first.Reads.Snapshot().ReadsFailed == cycle && second.Reads.Snapshot().ReadsFailed == cycle);
            Assert.Equal(cycle, slave.Requests.Count(request => request.Unit == 1));
            if (cycle < 3)
            { harness.Time.Advance(TimeSpan.FromSeconds(1)); }
        }
        Assert.Equal(InputReadHealth.Offline, first.Reads.Snapshot().Health);
        Assert.Equal(InputReadHealth.Offline, second.Reads.Snapshot().Health);
        harness.Time.Advance(TimeSpan.FromSeconds(1));
        await healthy.Channel.Reader.ReadAsync(Token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.Equal(3, slave.Requests.Count(request => request.Unit == 1));
        Volatile.Write(ref silent, 0);
        harness.Time.Advance(TimeSpan.FromSeconds(1));
        await first.Channel.Reader.ReadAsync(Token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), Token);
        await second.Channel.Reader.ReadAsync(Token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), Token);
        await healthy.Channel.Reader.ReadAsync(Token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.Equal(InputReadHealth.Normal, first.Reads.Snapshot().Health);
        Assert.Equal(InputReadHealth.Normal, second.Reads.Snapshot().Health);
        Assert.Equal(5, slave.Requests.Count(request => request.Unit == 1));
        // 恢复后回到正常 1 秒周期，而不是继续离线退避。
        harness.Time.Advance(TimeSpan.FromSeconds(1));
        await first.Channel.Reader.ReadAsync(Token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), Token);
        await second.Channel.Reader.ReadAsync(Token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.Equal(7, slave.Requests.Count(request => request.Unit == 1));
        Assert.Equal(1, slave.ConnectionCount);
    }

    [Fact]
    public async Task SlowGroupTimeoutDoesNotSuppressFastGroupUntilSlowPeriodEnds()
    {
        await using var slave = ModbusTestSlave.Start(ModbusFraming.Tcp,
            request => new SlaveReply { Silent = request.Unit == 1 && request.Address == 0 });
        await using var harness = new Harness();
        var slow = harness.Add(Plan(slave, "device/slow", 1, period: 5000));
        var fast = harness.Add(Plan(slave, "device/fast", 1, key: "fast", address: 10));
        var sibling = harness.Add(Plan(slave, "sibling/fast", 2));
        harness.Activate();
        harness.Start();
        await sibling.Channel.Reader.ReadAsync(Token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), Token);
        await OutputPipelineTests.UntilAsync(() => slow.Reads.Snapshot().ReadsFailed == 1 && fast.Reads.Snapshot().ReadsFailed == 1);
        Assert.Equal(2, slave.RequestCount);
        harness.Time.Advance(TimeSpan.FromSeconds(1));
        var message = await fast.Channel.Reader.ReadAsync(Token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), Token);
        await sibling.Channel.Reader.ReadAsync(Token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.Equal(FieldValue.FromInt64(4106), message.Values["fast"]);
        Assert.Equal(InputReadHealth.Normal, fast.Reads.Snapshot().Health);
        Assert.Equal(1, slow.Reads.Snapshot().ReadsFailed);
        Assert.Equal(1, slave.Requests.Count(request => request.Unit == 1 && request.Address == 0));
        Assert.Equal(1, slave.ConnectionCount);
    }

    [Fact]
    public async Task ActualConnectFailuresUpdateDiagnosticsButBackoffSkipsDoNotInventAttempts()
    {
        await using var slave = ModbusTestSlave.Start(ModbusFraming.Tcp);
        var connector = new DeniedConnector();
        await using var harness = new Harness(connector);
        var input = harness.Add(Plan(slave, "device/fast", 1));
        var sibling = harness.Add(Plan(slave, "device/slow", 1, key: "other"));
        harness.Activate();
        harness.Start();
        await OutputPipelineTests.UntilAsync(() => input.Reads.Snapshot().ReadsFailed == 1 && sibling.Reads.Snapshot().ReadsFailed == 1);
        harness.Time.Advance(TimeSpan.FromSeconds(1));
        await OutputPipelineTests.UntilAsync(() => input.Reads.Snapshot().ReadsFailed == 2 && sibling.Reads.Snapshot().ReadsFailed == 2);
        harness.Time.Advance(TimeSpan.FromSeconds(2));
        await OutputPipelineTests.UntilAsync(() => input.Reads.Snapshot().ReadsFailed == 3 && sibling.Reads.Snapshot().ReadsFailed == 3);
        Assert.Equal(InputReadHealth.Offline, input.Reads.Snapshot().Health);
        Assert.Equal(InputReadHealth.Offline, sibling.Reads.Snapshot().Health);
        Assert.Equal(3, connector.Calls);
        Assert.Equal(0, slave.ConnectionCount);
        Assert.False(input.Channel.Reader.TryRead(out _));
    }

    private sealed class DeniedConnector : IOutboundConnector
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public ValueTask<Stream> ConnectAsync(string host, int port, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return ValueTask.FromException<Stream>(new OutboundAccessDeniedException());
        }
    }

    private static RuntimeUnitPlan Plan(ModbusTestSlave slave, string id, byte unit, string key = "value", bool rtu = false, int timeout = 500, int period = 1000, int address = 0) =>
        new(RuntimeUnitId.Create("input.modbus", id), JsonSerializer.SerializeToElement(new
        {
            connectionId = 1,
            host = "127.0.0.1",
            port = slave.Endpoint.Port,
            unitId = unit,
            periodMs = period,
            readTimeoutMs = timeout,
            rtuSilenceMs = 100,
            frameFormat = rtu ? "rtu-over-tcp" : "tcp",
            points = new[] { new { key, functionCode = 3, address, type = "uint16" } }
        }));

    private sealed class Harness : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        private readonly TaskCompletionSource _activation = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<Input> _inputs = [];
        private Task[] _runs = [];
        public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 10, 9, 0, 0, 0, TimeSpan.Zero));
        public InFlightBudget Budget { get; } = new(64L * 1024 * 1024);
        public IInputUnitFactory Factory { get; }
        public Harness(IOutboundConnector? connector = null) => Factory = Assert.Single(new ModbusPlanAdapterProvider(new SharedConnections(), connector ?? new OutboundConnector(new Loopback()), Time).Create().Inputs);
        public Input Add(RuntimeUnitPlan plan)
        {
            var parts = plan.Id.Key.Split('/');
            var channel = new BudgetedInputChannel(1, Budget);
            var reads = new InputReadStatus(parts[0], parts[1], new RecordingEventSink());
            var context = new InputUnitContext(parts[0], parts[1], Time, new FixedClockSyncStatus(ClockSyncState.NotSynchronized), reads, (InputMessageWriter)channel.Writer, _activation.Task);
            var unit = Factory.Build(plan, context);
            var input = new Input(unit, context, channel, reads);
            _inputs.Add(input);
            return input;
        }
        public void Start() => _runs = _inputs.Select(input => input.Unit.RunAsync(_stop.Token)).ToArray();
        public void Activate() => _activation.TrySetResult();
        public async Task StopAsync() { await _stop.CancelAsync(); await Task.WhenAll(_runs).WaitAsync(TimeSpan.FromSeconds(5), Token); }
        public async ValueTask DisposeAsync()
        {
            await StopAsync();
            foreach (var input in _inputs)
            {
                while (input.Channel.Reader.TryRead(out _))
                { }
                await input.Unit.DisposeAsync();
            }
            _stop.Dispose();
        }
    }
    private sealed record Input(IRuntimeUnit Unit, InputUnitContext Context, BudgetedInputChannel Channel, InputReadStatus Reads);
    private sealed class Loopback : IOutboundAddressPolicy { public bool IsAllowed(IPAddress address, int port) => IPAddress.IsLoopback(address); }
}
