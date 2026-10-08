using System.Collections.Immutable;

namespace TenonEdge.Adapters.Modbus;

internal readonly record struct ModbusReadPoint(ModbusAddress Address, int Width);
internal readonly record struct ModbusBlockPoint(int PointIndex, int Offset, int Width);
internal sealed record ModbusReadBlock(byte FunctionCode, ushort Start, ushort Count, ImmutableArray<ModbusBlockPoint> Points);

/// <summary>单个 UnitId、单个采集组的启用点位；不跨组/从站合并，不跨未配置地址，不拆分一个点位。
/// 返回的 PointIndex 对应传入点位序号；调用者按块内偏移切片后解码，不按排序后的位置分配字段。</summary>
internal static class ModbusReadPlanner
{
    internal static ImmutableArray<ModbusReadBlock> Build(IReadOnlyList<ModbusReadPoint> points,
        int maxRegisters = 125, int maxBits = 2000)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (maxRegisters is < 1 or > 125)
        { throw new ArgumentOutOfRangeException(nameof(maxRegisters)); }
        if (maxBits is < 1 or > 2000)
        { throw new ArgumentOutOfRangeException(nameof(maxBits)); }

        // 先校验完整输入，防止非法范围借重叠/合并被掩盖。排序只复制序号，不修改调用者点表。
        for (var index = 0; index < points.Count; index++)
        {
            var point = points[index];
            _ = ModbusAddress.FromOffset(point.Address.FunctionCode, point.Address.Offset, point.Width);
            var isBit = point.Address.FunctionCode <= 2;
            if ((isBit && point.Width != 1) || (!isBit && point.Width is not (1 or 2 or 4)))
            { throw new ArgumentException("线圈/离散输入宽度须为 1，寄存器点宽度须为 1、2 或 4。", nameof(points)); }
            if (point.Width > (isBit ? maxBits : maxRegisters))
            { throw new ArgumentException("单个点位超出设备读取上限，不能拆分读取。", nameof(points)); }
        }
        var sorted = Enumerable.Range(0, points.Count)
            .OrderBy(index => points[index].Address.FunctionCode)
            .ThenBy(index => points[index].Address.Offset)
            .ThenByDescending(index => points[index].Width);
        var blocks = ImmutableArray.CreateBuilder<ModbusReadBlock>();
        var members = ImmutableArray.CreateBuilder<ModbusBlockPoint>();
        byte function = 0;
        var start = 0;
        var end = 0; // exclusive，最高合法值 65536；不能用 ushort 存储。
        foreach (var index in sorted)
        {
            var point = points[index];
            var offset = (int)point.Address.Offset;
            var nextEnd = offset + point.Width;
            var limit = point.Address.FunctionCode <= 2 ? maxBits : maxRegisters;
            if (members.Count > 0 && (point.Address.FunctionCode != function || offset > end || Math.Max(end, nextEnd) - start > limit))
            {
                blocks.Add(new(function, (ushort)start, (ushort)(end - start), members.ToImmutable()));
                members.Clear();
            }
            if (members.Count == 0)
            {
                function = point.Address.FunctionCode;
                start = offset;
                end = nextEnd;
            }
            else
            { end = Math.Max(end, nextEnd); }
            members.Add(new(index, offset - start, point.Width));
        }
        if (members.Count > 0)
        { blocks.Add(new(function, (ushort)start, (ushort)(end - start), members.ToImmutable())); }
        return blocks.ToImmutable();
    }
}
