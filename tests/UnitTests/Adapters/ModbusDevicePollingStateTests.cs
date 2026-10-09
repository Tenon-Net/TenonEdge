using Microsoft.Extensions.Time.Testing;
using TenonEdge.Adapters.Modbus;

namespace TenonEdge.UnitTests.Adapters;

public sealed class ModbusDevicePollingStateTests
{
    private static readonly ModbusCycleResult Failure = new(false, [new(ModbusRequestOutcome.Timeout, true), new(ModbusRequestOutcome.Timeout, false)]);
    private static readonly ModbusCycleResult PartialSuccess = new(false, [new(ModbusRequestOutcome.Timeout, true), new(ModbusRequestOutcome.Success, true, Registers: [42])]);

    [Fact]
    public void WholeCyclesNotBlocksCountTowardOfflineAndAnySuccessRecovers()
    {
        var time = new FakeTimeProvider();
        var state = new ModbusDevicePollingState(TimeSpan.FromSeconds(1), time);
        for (var index = 1; index <= 3; index++)
        {
            state.Record(Failure);
            Assert.Equal(index, state.ConsecutiveFailures);
            Assert.Equal(index == 3, state.IsOffline);
        }
        Assert.Equal(TimeSpan.FromSeconds(2), state.RetryRemaining);
        time.Advance(TimeSpan.FromSeconds(2));
        Assert.True(state.CanRead);
        state.Record(PartialSuccess);
        Assert.False(state.IsOffline);
        Assert.Equal(0, state.ConsecutiveFailures);
        Assert.Equal(TimeSpan.Zero, state.RetryRemaining);
    }

    [Fact]
    public void BackoffDoublesCapsAtSixtyAndRestartsAfterRecovery()
    {
        var time = new FakeTimeProvider();
        var state = new ModbusDevicePollingState(TimeSpan.FromSeconds(1), time);
        state.Record(Failure);
        state.Record(Failure);
        foreach (var expected in new[] { 2, 4, 8, 16, 32, 60, 60 })
        {
            state.Record(Failure);
            Assert.Equal(TimeSpan.FromSeconds(expected), state.RetryRemaining);
            time.Advance(TimeSpan.FromSeconds(expected) - TimeSpan.FromTicks(1));
            Assert.False(state.CanRead);
            time.Advance(TimeSpan.FromTicks(1));
            Assert.True(state.CanRead);
        }
        state.Record(PartialSuccess);
        state.Record(Failure);
        state.Record(Failure);
        state.Record(Failure);
        Assert.Equal(TimeSpan.FromSeconds(2), state.RetryRemaining);
    }

    [Fact]
    public void OverrunAndConnectionBackoffAreNotDeviceEvidenceOrRecovery()
    {
        var time = new FakeTimeProvider();
        var state = new ModbusDevicePollingState(TimeSpan.FromSeconds(1), time);
        state.Record(Failure);
        for (var index = 0; index < 10; index++)
        {
            state.Record(new(true, []));
            state.Record(new(false, [new(ModbusRequestOutcome.ConnectionError, false)]));
        }
        Assert.Equal(1, state.ConsecutiveFailures);
        state.Record(Failure);
        state.Record(Failure);
        time.Advance(TimeSpan.FromSeconds(1));
        state.Record(new(true, []));
        Assert.True(state.IsOffline);
        Assert.Equal(TimeSpan.FromSeconds(1), state.RetryRemaining);
    }

    [Theory]
    [InlineData("DeviceException")]
    [InlineData("ReadError")]
    [InlineData("ConnectionError")]
    public void ActualWholeCycleErrorsCountWithoutTreatingThemAsConnectionTimeouts(string outcome)
    {
        var state = new ModbusDevicePollingState(TimeSpan.FromSeconds(1), new FakeTimeProvider());
        for (var index = 0; index < 3; index++)
        { state.Record(new(false, [new(Enum.Parse<ModbusRequestOutcome>(outcome), true)])); }
        Assert.True(state.IsOffline);
    }

    [Fact]
    public void DevicesDoNotShareFailureEvidence()
    {
        var time = new FakeTimeProvider();
        var failed = new ModbusDevicePollingState(TimeSpan.FromSeconds(1), time);
        var healthy = new ModbusDevicePollingState(TimeSpan.FromSeconds(1), time);
        for (var index = 0; index < 3; index++)
        { failed.Record(Failure); healthy.Record(PartialSuccess); }
        Assert.True(failed.IsOffline);
        Assert.False(healthy.IsOffline);
        Assert.True(healthy.CanRead);
    }

    [Theory]
    [InlineData(0.1, 1)]
    [InlineData(5, 10)]
    [InlineData(100, 60)]
    public void InitialDelayUsesTwicePeriodWithOneToSixtySecondBounds(double period, int delay)
    {
        var state = new ModbusDevicePollingState(TimeSpan.FromSeconds(period), new FakeTimeProvider());
        for (var index = 0; index < 3; index++)
        { state.Record(Failure); }
        Assert.Equal(TimeSpan.FromSeconds(delay), state.RetryRemaining);
    }
}
