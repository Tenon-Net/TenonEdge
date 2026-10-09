using System.Collections.Immutable;
using TenonEdge.Abstractions.Messages;

namespace TenonEdge.Adapters.Modbus;

/// <summary>一次构建的不可变点表与连续块规划；解码只使用本次读取结果，从不填入历史成功值。</summary>
internal sealed class ModbusPointTable
{
    private readonly ImmutableArray<ModbusPointSettings> _points;
    internal ImmutableArray<ModbusReadBlock> Blocks { get; }

    internal ModbusPointTable(ImmutableArray<ModbusPointSettings> points, int maxRegisters = 125, int maxBits = 2000)
    {
        if (points.IsDefaultOrEmpty || points.Length > 10000)
        { throw new ArgumentException("Modbus 点表须为 1—10000 项。", nameof(points)); }
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var enabled = ImmutableArray.CreateBuilder<ModbusPointSettings>();
        var reads = new List<ModbusReadPoint>();
        foreach (var point in points)
        {
            if (point is null)
            { throw new ArgumentException("Modbus 点位不能为空。", nameof(points)); }
            var read = point.Validate();
            if (!keys.Add(point.Key))
            { throw new ArgumentException("Modbus 点位 key 重复。", nameof(points)); }
            if (!point.Enabled)
            { continue; }
            enabled.Add(point);
            reads.Add(read);
        }
        if (enabled.Count == 0)
        { throw new ArgumentException("运行采集组须至少有一个启用点位。", nameof(points)); }
        _points = enabled.ToImmutable();
        Blocks = ModbusReadPlanner.Build(reads, maxRegisters, maxBits);
    }

    internal EdgeMessage? Decode(ModbusCycleResult cycle, string inputKey, string deviceKey,
        DateTimeOffset receivedAt, TimeSource timeSource)
    {
        ArgumentNullException.ThrowIfNull(cycle);
        if (cycle.Overrun)
        { return null; }
        if (cycle.Blocks.IsDefault || cycle.Blocks.Length != Blocks.Length)
        { throw new ArgumentException("Modbus 周期结果与点表不匹配。", nameof(cycle)); }
        if (!cycle.HasSuccessfulRead)
        { return null; }
        var values = new List<KeyValuePair<string, FieldValue>>(_points.Length);
        var quality = new List<KeyValuePair<string, QualityCode>>();
        for (var index = 0; index < Blocks.Length; index++)
        {
            var block = Blocks[index];
            var result = cycle.Blocks[index];
            if (!Enum.IsDefined(result.Outcome))
            { throw new ArgumentException("Modbus 读取结果无效。", nameof(cycle)); }
            if (result.Outcome == ModbusRequestOutcome.Success && (!result.WasRequested ||
                (block.FunctionCode <= 2 ? result.Bits?.Length : result.Registers?.Length) != block.Count))
            { throw new ArgumentException("Modbus 成功结果长度与读取块不匹配。", nameof(cycle)); }
            foreach (var member in block.Points)
            {
                var point = _points[member.PointIndex];
                if (result.Outcome != ModbusRequestOutcome.Success)
                {
                    quality.Add(new(point.Key, result.Outcome switch
                    {
                        ModbusRequestOutcome.Timeout => QualityCode.ReadTimeout,
                        ModbusRequestOutcome.DeviceException => QualityCode.DeviceException,
                        _ => QualityCode.ReadError
                    }));
                    continue;
                }
                var decoded = block.FunctionCode <= 2 ? ModbusRegisterDecoder.DecodeCoil(result.Bits![member.Offset])
                    : ModbusRegisterDecoder.Decode(result.Registers.AsSpan(member.Offset, member.Width), point.RegisterType, point.Order, point.Bit);
                if (decoded.Error is { } error)
                { quality.Add(new(point.Key, error)); }
                else
                { values.Add(new(point.Key, decoded.Value!)); }
            }
        }
        // 读取成功但数值非有限时，输出明确的质量结果，不伪造 good 值；整组通信失败则上面已返回 null。
        return new EdgeMessage(Guid.CreateVersion7(receivedAt), inputKey, deviceKey, receivedAt, timeSource, receivedAt, values, quality);
    }
}
