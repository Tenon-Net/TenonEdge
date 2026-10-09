using TenonEdge.Abstractions.Messages;
using TenonEdge.Adapters.Modbus;

namespace TenonEdge.UnitTests.Adapters;

public sealed class ModbusPointTableTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 0, 0, 0, TimeSpan.Zero);
    private static ModbusPointSettings Point(string key, int address) => new() { Key = key, Address = address, FunctionCode = 3, Type = "uint16" };

    [Fact]
    public void UnsortedOverlappingPointsDecodeByOriginalIdentityAndDisabledGapsAreNotRead()
    {
        var table = new ModbusPointTable([
            Point("later", 10), Point("first", 0), Point("bit", 0) with { Type = "bit", Bit = 12 },
            Point("disabled", 1) with { Enabled = false }]);
        Assert.Equal(2, table.Blocks.Length);
        Assert.All(table.Blocks, block => Assert.Equal((ushort)1, block.Count));
        var message = table.Decode(new(false, [new(ModbusRequestOutcome.Success, true, Registers: [4096]),
            new(ModbusRequestOutcome.Success, true, Registers: [4106])]), "fast", "device", Now, TimeSource.GatewayUnsynced)!;
        Assert.Equal(FieldValue.FromInt64(4096), message.Values["first"]);
        Assert.Equal(FieldValue.FromInt64(4106), message.Values["later"]);
        Assert.Equal(FieldValue.FromBoolean(true), message.Values["bit"]);
        Assert.False(message.Values.ContainsKey("disabled"));
        Assert.Empty(message.Quality);
        Assert.Equal(Now, message.CollectedAt);
        Assert.Equal(Now, message.ReceivedAt);
        Assert.Equal(TimeSource.GatewayUnsynced, message.TimeSource);
    }

    [Theory]
    [InlineData("Timeout", "ReadTimeout")]
    [InlineData("DeviceException", "DeviceException")]
    [InlineData("ReadError", "ReadError")]
    [InlineData("ConnectionError", "ReadError")]
    public void FailedFieldsAreOmittedAndNeverUsePreviousSuccessfulValues(string outcome, string quality)
    {
        var table = new ModbusPointTable([Point("first", 0), Point("second", 10)]);
        var success = new ModbusReadResult(ModbusRequestOutcome.Success, true, Registers: [42]);
        var earlier = table.Decode(new(false, [success, success]), "fast", "device", Now, TimeSource.Gateway)!;
        var current = table.Decode(new(false, [success, new(Enum.Parse<ModbusRequestOutcome>(outcome), false)]),
            "fast", "device", Now.AddSeconds(1), TimeSource.Gateway)!;
        Assert.Single(current.Values);
        Assert.False(current.Values.ContainsKey("second"));
        Assert.Equal(Enum.Parse<QualityCode>(quality), current.Quality["second"]);
        Assert.Equal(2, earlier.Values.Count);
        Assert.NotEqual(earlier.SourceMessageId, current.SourceMessageId);
    }

    [Fact]
    public void EntireCommunicationFailureAndOverrunDoNotProduceMessages()
    {
        var table = new ModbusPointTable([Point("value", 0)]);
        Assert.Null(table.Decode(new(false, [new(ModbusRequestOutcome.Timeout, true)]), "fast", "device", Now, TimeSource.Gateway));
        Assert.Null(table.Decode(new(true, []), "fast", "device", Now, TimeSource.Gateway));
    }

    [Fact]
    public void PreservesUnsigned64BitAndMarksNonFiniteWithoutZeroSubstitution()
    {
        var table = new ModbusPointTable([
            Point("large", 0) with { Type = "uint64", ByteOrder = "ABCD" },
            Point("bad", 4) with { Type = "float32", ByteOrder = "ABCD" }]);
        var message = table.Decode(new(false, [new(ModbusRequestOutcome.Success, true,
            Registers: [0xffff, 0xffff, 0xffff, 0xffff, 0x7f80, 0])]), "fast", "device", Now, TimeSource.Gateway)!;
        Assert.Equal(FieldValue.FromUInt64(ulong.MaxValue), message.Values["large"]);
        Assert.False(message.Values.ContainsKey("bad"));
        Assert.Equal(QualityCode.NonFinite, message.Quality["bad"]);
    }

    [Fact]
    public void CoilBitsUseTheirPlannedOffsets()
    {
        var table = new ModbusPointTable([Point("b", 6) with { FunctionCode = 2, Type = "bool" }, Point("a", 5) with { FunctionCode = 2, Type = "bool" }]);
        var message = table.Decode(new(false, [new(ModbusRequestOutcome.Success, true, Bits: [false, true])]), "fast", "device", Now, TimeSource.Gateway)!;
        Assert.Equal(FieldValue.FromBoolean(false), message.Values["a"]);
        Assert.Equal(FieldValue.FromBoolean(true), message.Values["b"]);
    }

    [Fact]
    public void RejectsDuplicateKeysEmptyEnabledTableAndMalformedSuccess()
    {
        Assert.Throws<ArgumentException>(() => new ModbusPointTable([Point("same", 0), Point("same", 1)]));
        Assert.Throws<ArgumentException>(() => new ModbusPointTable([Point("disabled", 0) with { Enabled = false }]));
        var table = new ModbusPointTable([Point("value", 0)]);
        Assert.Throws<ArgumentException>(() => table.Decode(new(false, []), "fast", "device", Now, TimeSource.Gateway));
        Assert.Throws<ArgumentException>(() => table.Decode(new(false, [new(ModbusRequestOutcome.Success, true, Registers: [])]), "fast", "device", Now, TimeSource.Gateway));
    }
}
