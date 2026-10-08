using TenonEdge.Abstractions.Messages;
using TenonEdge.Adapters.Modbus;

namespace TenonEdge.UnitTests.Adapters;

public sealed class ModbusReadPlannerTests
{
    [Fact]
    public void UnsortedAdjacentAndOverlappingPointsKeepTheirOriginalIdentity()
    {
        ModbusReadPoint[] points = [Point(3, 12, 2), Point(3, 10, 2), Point(3, 11), Point(3, 10)];
        var original = points.ToArray();
        var block = Assert.Single(ModbusReadPlanner.Build(points));
        Assert.Equal((byte)3, block.FunctionCode);
        Assert.Equal((ushort)10, block.Start);
        Assert.Equal((ushort)4, block.Count);
        Assert.Equal(new[] { new ModbusBlockPoint(1, 0, 2), new(3, 0, 1), new(2, 1, 1), new(0, 2, 2) }, block.Points);
        Assert.Equal(original, points);
    }

    [Fact]
    public void GapsAndDifferentFunctionCodesAlwaysStartNewBlocks()
    {
        // 地址 1 没有启用点位，不可为减少请求而读过它。
        ModbusReadPoint[] points = [Point(3, 0), Point(3, 2), Point(4, 2), Point(1, 2), Point(2, 2)];
        var blocks = ModbusReadPlanner.Build(points);
        Assert.Equal(5, blocks.Length);
        Assert.All(blocks, block => Assert.Equal((ushort)1, block.Count));
        Assert.Equal(new byte[] { 1, 2, 3, 3, 4 }, blocks.Select(block => block.FunctionCode));
    }

    [Theory]
    [InlineData(1, 2000)]
    [InlineData(2, 2000)]
    [InlineData(3, 125)]
    [InlineData(4, 125)]
    public void ProtocolLimitSplitsAtTheFirstExcessPoint(byte function, int limit)
    {
        var points = Enumerable.Range(0, limit + 1).Select(offset => Point(function, offset)).ToArray();
        var blocks = ModbusReadPlanner.Build(points);
        Assert.Equal(2, blocks.Length);
        Assert.Equal((ushort)limit, blocks[0].Count);
        Assert.Equal((ushort)limit, blocks[1].Start);
        Assert.Equal((ushort)1, blocks[1].Count);
    }

    [Fact]
    public void MultiRegisterPointIsNotSplitAtLimitEvenWhenItOverlapsPriorBlock()
    {
        var blocks = ModbusReadPlanner.Build([Point(3, 0, 4), Point(3, 3, 4), Point(3, 7)], maxRegisters: 5);
        Assert.Equal(2, blocks.Length);
        Assert.Equal((ushort)4, blocks[0].Count);
        Assert.Equal((ushort)3, blocks[1].Start);
        Assert.Equal((ushort)5, blocks[1].Count);
        Assert.Equal(new[] { new ModbusBlockPoint(1, 0, 4), new(2, 4, 1) }, blocks[1].Points);
    }

    [Fact]
    public void DeviceLimitsAndLastAddressAreRespected()
    {
        var bits = ModbusReadPlanner.Build([Point(1, 65534), Point(1, 65535)], maxBits: 1);
        Assert.Equal(2, bits.Length);
        Assert.Equal((ushort)65535, bits[1].Start);
        var registers = Assert.Single(ModbusReadPlanner.Build([Point(3, 65532, 4), Point(3, 65535)]));
        Assert.Equal((ushort)4, registers.Count);
        Assert.Equal(3, registers.Points[1].Offset);
        Assert.Empty(ModbusReadPlanner.Build([]));
    }

    [Fact]
    public void InvalidPointsAndLimitsAreRejectedRatherThanTruncated()
    {
        Assert.Throws<ArgumentNullException>(() => ModbusReadPlanner.Build(null!));
        foreach (var limit in new[] { 0, -1, 126, int.MaxValue })
        { Assert.Throws<ArgumentOutOfRangeException>(() => ModbusReadPlanner.Build([], maxRegisters: limit)); }
        foreach (var limit in new[] { 0, -1, 2001, int.MaxValue })
        { Assert.Throws<ArgumentOutOfRangeException>(() => ModbusReadPlanner.Build([], maxBits: limit)); }
        foreach (var point in new[] { Point(3, 65535, 2), Point(0, 0), Point(5, 0), Point(3, 0, 0), Point(3, 0, 3), Point(1, 0, 2) })
        { Assert.ThrowsAny<ArgumentException>(() => ModbusReadPlanner.Build([point])); }
        Assert.Throws<ArgumentException>(() => ModbusReadPlanner.Build([Point(3, 0, 4)], maxRegisters: 3));
    }

    [Fact]
    public void SeededPointSetsCoverEveryPointExactlyOnceWithoutReadingAnyGap()
    {
        var random = new Random(20261009);
        for (var trial = 0; trial < 200; trial++)
        {
            var points = Enumerable.Range(0, 100).Select(_ =>
            {
                var function = (byte)random.Next(1, 5);
                return Point(function, random.Next(0, 256), function <= 2 ? 1 : 1 << random.Next(0, 3));
            }).ToArray();
            var blocks = ModbusReadPlanner.Build(points, maxRegisters: 8, maxBits: 13);
            Assert.Equal(Enumerable.Range(0, points.Length), blocks.SelectMany(block => block.Points).Select(point => point.PointIndex).Order());
            foreach (var block in blocks)
            {
                Assert.InRange((int)block.Count, 1, block.FunctionCode <= 2 ? 13 : 8);
                foreach (var member in block.Points)
                {
                    var point = points[member.PointIndex];
                    Assert.Equal(point.Address.FunctionCode, block.FunctionCode);
                    Assert.Equal(point.Address.Offset, block.Start + member.Offset);
                    Assert.Equal(point.Width, member.Width);
                    Assert.InRange(member.Offset, 0, block.Count - member.Width);
                }
                for (var offset = (int)block.Start; offset < block.Start + block.Count; offset++)
                {
                    Assert.Contains(block.Points, member =>
                        points[member.PointIndex].Address.Offset <= offset &&
                        points[member.PointIndex].Address.Offset + member.Width > offset);
                }
            }
        }
    }

    [Fact]
    public void BlockSlicesDecodeToOriginalFieldsRatherThanSortedPositions()
    {
        // 一个真实线序的人工寄存器区：float32 1.0、uint64 > 2^53、int16 -1。
        ushort[] registers = [0x3f80, 0, 0x0123, 0x4567, 0x89ab, 0xcdef, 0xffff];
        ModbusReadPoint[] points = [Point(3, 16), Point(3, 12, 4), Point(3, 10, 2), Point(3, 16)];
        ModbusRegisterType[] types = [ModbusRegisterType.Int16, ModbusRegisterType.UInt64,
            ModbusRegisterType.Float32, ModbusRegisterType.Bit];
        ModbusDecodedValue[] values = new ModbusDecodedValue[points.Length];
        var blocks = ModbusReadPlanner.Build(points, maxRegisters: 4);
        Assert.Equal(3, blocks.Length);
        foreach (var block in blocks)
        {
            var response = registers.AsSpan(block.Start - 10, block.Count);
            foreach (var member in block.Points)
            {
                values[member.PointIndex] = ModbusRegisterDecoder.Decode(response.Slice(member.Offset, member.Width),
                    types[member.PointIndex], member.Width > 1 ? ModbusByteOrder.Abcd : null,
                    types[member.PointIndex] == ModbusRegisterType.Bit ? 15 : null);
            }
        }
        Assert.All(values, value => Assert.Null(value.Error));
        Assert.Equal(FieldValue.FromInt64(-1), values[0].Value);
        Assert.Equal(FieldValue.FromUInt64(81985529216486895), values[1].Value);
        Assert.True(FieldValue.TryFromSingle(1, out var single));
        Assert.Equal(single, values[2].Value);
        Assert.Equal(FieldValue.FromBoolean(true), values[3].Value);
    }

    private static ModbusReadPoint Point(byte function, int offset, int width = 1) => new(new(function, (ushort)offset), width);
}
