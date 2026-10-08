using System.Globalization;

namespace TenonEdge.Adapters.Modbus;

/// <summary>只读地址的规范化；显式区分协议偏移、无前缀寄存器号及完整手册编号，避免数字歧义。</summary>
internal readonly record struct ModbusAddress(byte FunctionCode, ushort Offset)
{
    internal static ModbusAddress FromOffset(byte functionCode, int offset, int width = 1)
    {
        if (functionCode is < 1 or > 4)
        { throw new ArgumentOutOfRangeException(nameof(functionCode)); }
        if (offset is < 0 or > ushort.MaxValue)
        { throw new ArgumentOutOfRangeException(nameof(offset)); }
        if (width < 1 || width > 65536 - offset)
        { throw new ArgumentOutOfRangeException(nameof(width), "点位占用范围不能越过地址 65535。"); }
        return new(functionCode, (ushort)offset);
    }

    internal static ModbusAddress FromRegisterNumber(byte functionCode, int number, int width = 1)
    {
        if (number is < 1 or > 65536)
        { throw new ArgumentOutOfRangeException(nameof(number)); }
        return FromOffset(functionCode, number - 1, width);
    }

    internal static ModbusAddress FromManualReference(string reference, int width = 1)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (reference.Length is not (5 or 6) || reference.Any(c => c is < '0' or > '9'))
        { throw new ArgumentException("完整手册编号必须为 5 或 6 位 ASCII 数字，保留前导零。", nameof(reference)); }
        byte function = reference[0] switch
        {
            '0' => 1,
            '1' => 2,
            '3' => 4,
            '4' => 3,
            _ => throw new ArgumentException("手册编号前缀必须为 0、1、3 或 4。", nameof(reference)),
        };
        var number = int.Parse(reference.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture);
        return FromRegisterNumber(function, number, width);
    }
}
