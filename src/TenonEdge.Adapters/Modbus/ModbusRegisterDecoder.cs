using TenonEdge.Abstractions.Messages;

namespace TenonEdge.Adapters.Modbus;

internal enum ModbusRegisterType { Bit, Int16, UInt16, Int32, UInt32, Float32, Int64, UInt64, Float64 }

/// <summary>64 位分别对应 ABCDEFGH、GHEFCDAB、BADCFEHG、HGFEDCBA；交换字指逆序全部 16 位寄存器。</summary>
internal enum ModbusByteOrder { Abcd, Cdab, Badc, Dcba }

internal readonly record struct ModbusDecodedValue(FieldValue? Value, QualityCode? Error);

/// <summary>协议库交出的 ushort 是每个寄存器的数值，而不是主机内存中的字节数组。
/// 只读解码，不决定 UnitId、地址写法、网络协议或调度；不使用本机端序，不经过 double 中转整数。</summary>
internal static class ModbusRegisterDecoder
{
    internal static int RegisterCount(ModbusRegisterType type) => type switch
    {
        ModbusRegisterType.Bit or ModbusRegisterType.Int16 or ModbusRegisterType.UInt16 => 1,
        ModbusRegisterType.Int32 or ModbusRegisterType.UInt32 or ModbusRegisterType.Float32 => 2,
        ModbusRegisterType.Int64 or ModbusRegisterType.UInt64 or ModbusRegisterType.Float64 => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    internal static ModbusDecodedValue DecodeCoil(bool value) => new(FieldValue.FromBoolean(value), null);

    internal static ModbusDecodedValue Decode(ReadOnlySpan<ushort> registers, ModbusRegisterType type,
        ModbusByteOrder? byteOrder = null, int? bit = null)
    {
        var count = RegisterCount(type);
        if (registers.Length != count)
        { throw new ArgumentException("寄存器数量与点位类型不匹配，不能补零或截断解码。", nameof(registers)); }
        if (count > 1 && (byteOrder is null || !Enum.IsDefined(byteOrder.Value)))
        { throw new ArgumentException("32/64 位点位必须指定有效字节序。", nameof(byteOrder)); }
        if (count == 1 && byteOrder is not null)
        { throw new ArgumentException("16 位寄存器不接受多寄存器字节序。", nameof(byteOrder)); }
        if (type == ModbusRegisterType.Bit)
        {
            if (bit is null or < 0 or > 15)
            { throw new ArgumentOutOfRangeException(nameof(bit), "寄存器位号须为 0—15，0 为最低位。"); }
            return new(FieldValue.FromBoolean((registers[0] & (1 << bit.Value)) != 0), null);
        }
        if (bit is not null)
        { throw new ArgumentException("非取位点不能指定寄存器位号。", nameof(bit)); }

        ulong bits = 0;
        for (var index = 0; index < count; index++)
        {
            var source = byteOrder is ModbusByteOrder.Cdab or ModbusByteOrder.Dcba ? count - 1 - index : index;
            var word = registers[source];
            if (byteOrder is ModbusByteOrder.Badc or ModbusByteOrder.Dcba)
            { word = (ushort)((word >> 8) | (word << 8)); }
            bits = (bits << 16) | word;
        }
        return type switch
        {
            ModbusRegisterType.Int16 => new(FieldValue.FromInt64(unchecked((short)bits)), null),
            ModbusRegisterType.UInt16 => new(FieldValue.FromInt64((ushort)bits), null),
            ModbusRegisterType.Int32 => new(FieldValue.FromInt64(unchecked((int)bits)), null),
            ModbusRegisterType.UInt32 => new(FieldValue.FromInt64((uint)bits), null),
            ModbusRegisterType.Int64 => new(FieldValue.FromInt64(unchecked((long)bits)), null),
            ModbusRegisterType.UInt64 => new(FieldValue.FromUInt64(bits), null),
            ModbusRegisterType.Float32 => FieldValue.TryFromSingle(BitConverter.Int32BitsToSingle(unchecked((int)bits)), out var single)
                ? new(single, null) : new(null, QualityCode.NonFinite),
            ModbusRegisterType.Float64 => FieldValue.TryFromDouble(BitConverter.Int64BitsToDouble(unchecked((long)bits)), out var number)
                ? new(number, null) : new(null, QualityCode.NonFinite),
            _ => throw new ArgumentOutOfRangeException(nameof(type)),
        };
    }
}
