using TenonEdge.Adapters.Modbus;

namespace TenonEdge.UnitTests.Adapters;

public sealed class ModbusConnectionHealthTests
{
    [Fact]
    public void OneSilentSlaveDoesNotMakeUntriedOrRespondingSlavesFail()
    {
        var health = new ModbusConnectionHealth([1, 2, 3]);
        Assert.False(health.AllUnitsTimedOut);
        for (var cycle = 0; cycle < 10; cycle++)
        { Assert.False(health.Record(1, ModbusRequestOutcome.Timeout)); }
        Assert.False(health.Record(2, ModbusRequestOutcome.Timeout));
        Assert.False(health.Record(3, ModbusRequestOutcome.Success));
        Assert.False(health.Record(1, ModbusRequestOutcome.Timeout));
        Assert.True(health.Record(3, ModbusRequestOutcome.Timeout));
    }

    [Theory]
    [InlineData("Success")]
    [InlineData("DeviceException")]
    [InlineData("ReadError")]
    public void LatestNonTimeoutRequestClearsOnlyThatSlavesTimeout(string outcome)
    {
        var health = new ModbusConnectionHealth([1, 2]);
        Assert.False(health.Record(1, ModbusRequestOutcome.Timeout));
        Assert.True(health.Record(2, ModbusRequestOutcome.Timeout));
        Assert.False(health.Record(1, Enum.Parse<ModbusRequestOutcome>(outcome)));
        Assert.True(health.Record(1, ModbusRequestOutcome.Timeout));
    }

    [Fact]
    public void ConnectionErrorRequestsReconnectWithoutFabricatingOtherSlaveTimeouts()
    {
        var health = new ModbusConnectionHealth([1, 2]);
        Assert.True(health.Record(1, ModbusRequestOutcome.ConnectionError));
        Assert.False(health.AllUnitsTimedOut);
        Assert.False(health.Record(2, ModbusRequestOutcome.Timeout));
        Assert.True(health.Record(1, ModbusRequestOutcome.Timeout));
    }

    [Fact]
    public void ReplacingConnectionRequiresFreshEvidenceForEveryEnabledSlave()
    {
        var health = new ModbusConnectionHealth([1, 2]);
        health.Record(1, ModbusRequestOutcome.Timeout);
        health.Record(2, ModbusRequestOutcome.Timeout);
        Assert.True(health.AllUnitsTimedOut);
        health.ConnectionReplaced();
        Assert.False(health.AllUnitsTimedOut);
        Assert.False(health.Record(1, ModbusRequestOutcome.Timeout));
        Assert.True(health.Record(2, ModbusRequestOutcome.Timeout));
        health.ConnectionReplaced();
        Assert.False(health.Record(2, ModbusRequestOutcome.Timeout));
    }

    [Fact]
    public void DuplicateGroupsDoNotMultiplyDevicesAndConnectionsDoNotShareHealth()
    {
        var first = new ModbusConnectionHealth([1, 1]);
        var second = new ModbusConnectionHealth([1]);
        Assert.True(first.Record(1, ModbusRequestOutcome.Timeout));
        Assert.False(second.AllUnitsTimedOut);
        Assert.False(second.Record(1, ModbusRequestOutcome.Success));
        Assert.True(first.AllUnitsTimedOut);
    }

    [Fact]
    public void InvalidInputsAreRejectedWithoutChangingObservedState()
    {
        Assert.Throws<ArgumentNullException>(() => new ModbusConnectionHealth(null!));
        Assert.Throws<ArgumentException>(() => new ModbusConnectionHealth([]));
        var health = new ModbusConnectionHealth([1]);
        Assert.Throws<ArgumentOutOfRangeException>(() => health.Record(2, ModbusRequestOutcome.Timeout));
        Assert.Throws<ArgumentOutOfRangeException>(() => health.Record(1, (ModbusRequestOutcome)99));
        Assert.False(health.AllUnitsTimedOut);
        Assert.True(health.Record(1, ModbusRequestOutcome.Timeout));
    }
}
