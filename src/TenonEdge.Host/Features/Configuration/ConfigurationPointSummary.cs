using TenonEdge.Adapters.Modbus;

namespace TenonEdge.Host.Features.Configuration;

internal sealed record ConfigurationPointSummary(long DeviceId, int Added, int Modified, int Removed)
{
    /// <summary>Modbus 点位按设备汇总；来源 ID + 点 key 为身份，排序改变不算修改。
    /// JSON 字段和模拟源配置仍通过来源对象的完整差异展示，不冒充 Modbus 点位。</summary>
    public static ConfigurationPointSummary[] Between(ConfigurationCheckpoint before, ConfigurationCheckpoint after)
    {
        var previous = Points(before);
        var current = Points(after);
        var counts = new Dictionary<long, (int Added, int Modified, int Removed)>();
        foreach (var key in previous.Keys.Union(current.Keys))
        {
            var existed = previous.TryGetValue(key, out var left);
            var exists = current.TryGetValue(key, out var right);
            if (existed && exists && left == right)
            { continue; }
            var count = counts.GetValueOrDefault(key.DeviceId);
            counts[key.DeviceId] = !existed ? (count.Added + 1, count.Modified, count.Removed)
                : !exists ? (count.Added, count.Modified, count.Removed + 1)
                : (count.Added, count.Modified + 1, count.Removed);
        }
        return counts.OrderBy(pair => pair.Key).Select(pair => new ConfigurationPointSummary(pair.Key,
            pair.Value.Added, pair.Value.Modified, pair.Value.Removed)).ToArray();
    }

    private static Dictionary<(long DeviceId, long InputId, string Key), ModbusPointSettings> Points(ConfigurationCheckpoint draft) =>
        draft.Inputs.Where(input => input.Modbus is not null).SelectMany(input => input.Modbus!.Points.Select(point =>
            (Identity: (input.DeviceId, input.Id, point.Key), Point: point))).ToDictionary(value => value.Identity, value => value.Point);
}
