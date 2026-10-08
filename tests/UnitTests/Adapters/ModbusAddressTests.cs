using TenonEdge.Adapters.Modbus;

namespace TenonEdge.UnitTests.Adapters;

public sealed class ModbusAddressTests
{
    [Theory]
    [InlineData("00001", 1, 0)]
    [InlineData("10001", 2, 0)]
    [InlineData("30001", 4, 0)]
    [InlineData("40001", 3, 0)]
    [InlineData("400001", 3, 0)]
    [InlineData("465536", 3, 65535)]
    [InlineData("065536", 1, 65535)]
    [InlineData("39999", 4, 9998)]
    public void ManualReferenceMatchesExplicitOffsetAndRegisterNumber(string reference, byte function, int offset)
    {
        var actual = ModbusAddress.FromManualReference(reference);
        Assert.Equal(ModbusAddress.FromOffset(function, offset), actual);
        Assert.Equal(ModbusAddress.FromRegisterNumber(function, offset + 1), actual);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public void EntirePointMustFitAddressSpace(int width)
    {
        Assert.Equal((ushort)(65536 - width), ModbusAddress.FromOffset(3, 65536 - width, width).Offset);
        Assert.Throws<ArgumentOutOfRangeException>(() => ModbusAddress.FromOffset(3, 65537 - width, width));
        Assert.Throws<ArgumentOutOfRangeException>(() => ModbusAddress.FromRegisterNumber(3, 65538 - width, width));
    }

    [Theory]
    [InlineData("")]
    [InlineData("4001")]
    [InlineData("4000001")]
    [InlineData("20001")]
    [InlineData("50001")]
    [InlineData("40000")]
    [InlineData("400000")]
    [InlineData("465537")]
    [InlineData("4+001")]
    [InlineData(" 40001")]
    [InlineData("40001 ")]
    [InlineData("４０００１")]
    public void InvalidReferenceIsNotGuessedOrWrapped(string reference) =>
        Assert.ThrowsAny<ArgumentException>(() => ModbusAddress.FromManualReference(reference));

    [Fact]
    public void ExplicitModesDoNotGuessPrefixesAndRejectInvalidRanges()
    {
        Assert.Equal((ushort)40001, ModbusAddress.FromOffset(3, 40001).Offset);
        Assert.Equal((ushort)40000, ModbusAddress.FromRegisterNumber(3, 40001).Offset);
        Assert.Throws<ArgumentNullException>(() => ModbusAddress.FromManualReference(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => ModbusAddress.FromOffset(0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ModbusAddress.FromOffset(5, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ModbusAddress.FromOffset(3, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => ModbusAddress.FromOffset(3, int.MaxValue));
        Assert.Throws<ArgumentOutOfRangeException>(() => ModbusAddress.FromOffset(3, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ModbusAddress.FromOffset(3, 0, int.MaxValue));
        Assert.Throws<ArgumentOutOfRangeException>(() => ModbusAddress.FromRegisterNumber(3, int.MinValue));
    }
}
