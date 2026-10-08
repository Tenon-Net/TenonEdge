using System.Buffers;
using System.Text;
using System.Text.Json;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Adapters.Modbus;

namespace TenonEdge.UnitTests.Adapters;

public sealed class ModbusRegisterDecoderTests
{
    // 手写线序向量与已知十进制值；不使用被测排列算法反向生成期望输入。
    public static TheoryData<string, string, ushort[], string> KnownValues => new()
    {
        { "Int32", "Abcd", [0x89ab, 0xcdef], "-1985229329" },
        { "Int32", "Cdab", [0xcdef, 0x89ab], "-1985229329" },
        { "Int32", "Badc", [0xab89, 0xefcd], "-1985229329" },
        { "Int32", "Dcba", [0xefcd, 0xab89], "-1985229329" },
        { "UInt32", "Abcd", [0x89ab, 0xcdef], "2309737967" },
        { "UInt32", "Cdab", [0xcdef, 0x89ab], "2309737967" },
        { "UInt32", "Badc", [0xab89, 0xefcd], "2309737967" },
        { "UInt32", "Dcba", [0xefcd, 0xab89], "2309737967" },
        { "Float32", "Abcd", [0x3dcc, 0xcccd], "0.1" },
        { "Float32", "Cdab", [0xcccd, 0x3dcc], "0.1" },
        { "Float32", "Badc", [0xcc3d, 0xcdcc], "0.1" },
        { "Float32", "Dcba", [0xcdcc, 0xcc3d], "0.1" },
        { "Int64", "Abcd", [0xfedc, 0xba98, 0x7654, 0x3210], "-81985529216486896" },
        { "Int64", "Cdab", [0x3210, 0x7654, 0xba98, 0xfedc], "-81985529216486896" },
        { "Int64", "Badc", [0xdcfe, 0x98ba, 0x5476, 0x1032], "-81985529216486896" },
        { "Int64", "Dcba", [0x1032, 0x5476, 0x98ba, 0xdcfe], "-81985529216486896" },
        { "UInt64", "Abcd", [0x0123, 0x4567, 0x89ab, 0xcdef], "81985529216486895" },
        { "UInt64", "Cdab", [0xcdef, 0x89ab, 0x4567, 0x0123], "81985529216486895" },
        { "UInt64", "Badc", [0x2301, 0x6745, 0xab89, 0xefcd], "81985529216486895" },
        { "UInt64", "Dcba", [0xefcd, 0xab89, 0x6745, 0x2301], "81985529216486895" },
        { "Float64", "Abcd", [0xc05e, 0xe000, 0x0000, 0x0000], "-123.5" },
        { "Float64", "Cdab", [0x0000, 0x0000, 0xe000, 0xc05e], "-123.5" },
        { "Float64", "Badc", [0x5ec0, 0x00e0, 0x0000, 0x0000], "-123.5" },
        { "Float64", "Dcba", [0x0000, 0x0000, 0x00e0, 0x5ec0], "-123.5" },
    };

    [Theory]
    [MemberData(nameof(KnownValues))]
    public void KnownWireWordsDecodeExactlyAndPreserveTheOriginalRegisters(string type, string order, ushort[] words, string expected)
    {
        var original = words.ToArray();
        var decoded = ModbusRegisterDecoder.Decode(words, Enum.Parse<ModbusRegisterType>(type), Enum.Parse<ModbusByteOrder>(order));
        Assert.Null(decoded.Error);
        Assert.Equal(expected, Json(decoded.Value!));
        Assert.Equal(original, words);
    }

    [Theory]
    [InlineData("Int16", 0x8000, "-32768")]
    [InlineData("Int16", 0xffff, "-1")]
    [InlineData("Int16", 0x7fff, "32767")]
    [InlineData("UInt16", 0xffff, "65535")]
    [InlineData("UInt16", 0, "0")]
    public void OneRegisterHasNetworkWordSemantics(string type, ushort word, string expected)
    {
        var decoded = ModbusRegisterDecoder.Decode([word], Enum.Parse<ModbusRegisterType>(type));
        Assert.Null(decoded.Error);
        Assert.Equal(expected, Json(decoded.Value!));
    }

    [Fact]
    public void EveryRegisterBitUsesZeroAsLeastSignificantBit()
    {
        for (var bit = 0; bit < 16; bit++)
        {
            var set = ModbusRegisterDecoder.Decode([(ushort)(1 << bit)], ModbusRegisterType.Bit, bit: bit);
            var clear = ModbusRegisterDecoder.Decode([(ushort)~(1 << bit)], ModbusRegisterType.Bit, bit: bit);
            Assert.Equal("true", Json(set.Value!));
            Assert.Equal("false", Json(clear.Value!));
            Assert.Null(set.Error);
            Assert.Null(clear.Error);
        }
        Assert.Equal("true", Json(ModbusRegisterDecoder.DecodeCoil(true).Value!));
        Assert.Equal("false", Json(ModbusRegisterDecoder.DecodeCoil(false).Value!));
    }

    [Theory]
    [InlineData("Int64", 0x8000, "-9223372036854775808")]
    [InlineData("UInt64", 0x8000, "9223372036854775808")]
    public void IntegerHighBitDoesNotGoThroughDouble(string type, ushort highWord, string expected) =>
        Assert.Equal(expected, Json(ModbusRegisterDecoder.Decode([highWord, 0, 0, 0],
            Enum.Parse<ModbusRegisterType>(type), ModbusByteOrder.Abcd).Value!));

    [Fact]
    public void UnsignedMaximumAndNegativeFloatingZeroArePreserved()
    {
        Assert.Equal("18446744073709551615", Json(ModbusRegisterDecoder.Decode([0xffff, 0xffff, 0xffff, 0xffff],
            ModbusRegisterType.UInt64, ModbusByteOrder.Abcd).Value!));
        Assert.Equal("-0", Json(ModbusRegisterDecoder.Decode([0x8000, 0], ModbusRegisterType.Float32, ModbusByteOrder.Abcd).Value!));
        Assert.Equal("-0", Json(ModbusRegisterDecoder.Decode([0x8000, 0, 0, 0], ModbusRegisterType.Float64, ModbusByteOrder.Abcd).Value!));
    }

    [Theory]
    [InlineData("Float32", "Abcd", new ushort[] { 0x7fc0, 0x0000 })]
    [InlineData("Float32", "Cdab", new ushort[] { 0x0000, 0x7fc0 })]
    [InlineData("Float32", "Badc", new ushort[] { 0xc07f, 0x0000 })]
    [InlineData("Float32", "Dcba", new ushort[] { 0x0000, 0xc07f })]
    [InlineData("Float32", "Abcd", new ushort[] { 0x7f80, 0x0000 })]
    [InlineData("Float32", "Cdab", new ushort[] { 0x0000, 0x7f80 })]
    [InlineData("Float32", "Badc", new ushort[] { 0x807f, 0x0000 })]
    [InlineData("Float32", "Dcba", new ushort[] { 0x0000, 0x807f })]
    [InlineData("Float32", "Abcd", new ushort[] { 0xff80, 0x0000 })]
    [InlineData("Float32", "Cdab", new ushort[] { 0x0000, 0xff80 })]
    [InlineData("Float32", "Badc", new ushort[] { 0x80ff, 0x0000 })]
    [InlineData("Float32", "Dcba", new ushort[] { 0x0000, 0x80ff })]
    [InlineData("Float64", "Abcd", new ushort[] { 0x7ff8, 0x0000, 0x0000, 0x0000 })]
    [InlineData("Float64", "Cdab", new ushort[] { 0x0000, 0x0000, 0x0000, 0x7ff8 })]
    [InlineData("Float64", "Badc", new ushort[] { 0xf87f, 0x0000, 0x0000, 0x0000 })]
    [InlineData("Float64", "Dcba", new ushort[] { 0x0000, 0x0000, 0x0000, 0xf87f })]
    [InlineData("Float64", "Abcd", new ushort[] { 0x7ff0, 0x0000, 0x0000, 0x0000 })]
    [InlineData("Float64", "Cdab", new ushort[] { 0x0000, 0x0000, 0x0000, 0x7ff0 })]
    [InlineData("Float64", "Badc", new ushort[] { 0xf07f, 0x0000, 0x0000, 0x0000 })]
    [InlineData("Float64", "Dcba", new ushort[] { 0x0000, 0x0000, 0x0000, 0xf07f })]
    [InlineData("Float64", "Abcd", new ushort[] { 0xfff0, 0x0000, 0x0000, 0x0000 })]
    [InlineData("Float64", "Cdab", new ushort[] { 0x0000, 0x0000, 0x0000, 0xfff0 })]
    [InlineData("Float64", "Badc", new ushort[] { 0xf0ff, 0x0000, 0x0000, 0x0000 })]
    [InlineData("Float64", "Dcba", new ushort[] { 0x0000, 0x0000, 0x0000, 0xf0ff })]
    public void NonFiniteHasNoValueAndAnExplicitQualityReason(string type, string order, ushort[] words)
    {
        var decoded = ModbusRegisterDecoder.Decode(words, Enum.Parse<ModbusRegisterType>(type), Enum.Parse<ModbusByteOrder>(order));
        Assert.Null(decoded.Value);
        Assert.Equal(QualityCode.NonFinite, decoded.Error);
    }

    [Fact]
    public void InvalidConfigurationAndTruncatedOrExtraWordsAreNotSilentlyDecoded()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ModbusRegisterDecoder.RegisterCount((ModbusRegisterType)99));
        Assert.Throws<ArgumentException>(() => ModbusRegisterDecoder.Decode([1], ModbusRegisterType.Int32, ModbusByteOrder.Abcd));
        Assert.Throws<ArgumentException>(() => ModbusRegisterDecoder.Decode([1, 2, 3], ModbusRegisterType.Int32, ModbusByteOrder.Abcd));
        Assert.Throws<ArgumentException>(() => ModbusRegisterDecoder.Decode([1, 2], ModbusRegisterType.Int32));
        Assert.Throws<ArgumentException>(() => ModbusRegisterDecoder.Decode([1, 2], ModbusRegisterType.Int32, (ModbusByteOrder)99));
        Assert.Throws<ArgumentException>(() => ModbusRegisterDecoder.Decode([1], ModbusRegisterType.UInt16, ModbusByteOrder.Abcd));
        Assert.Throws<ArgumentException>(() => ModbusRegisterDecoder.Decode([1], ModbusRegisterType.Int16, bit: 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => ModbusRegisterDecoder.Decode([1], ModbusRegisterType.Bit));
        Assert.Throws<ArgumentOutOfRangeException>(() => ModbusRegisterDecoder.Decode([1], ModbusRegisterType.Bit, bit: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => ModbusRegisterDecoder.Decode([1], ModbusRegisterType.Bit, bit: 16));
    }

    private static string Json(FieldValue value)
    {
        var bytes = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(bytes))
        { value.WriteTo(writer); }
        return Encoding.UTF8.GetString(bytes.WrittenSpan);
    }
}
