using System.Text.Json;
using System.Text.Json.Serialization;

namespace TenonEdge.Host.Features.Configuration;

/// <summary>按稳定对象 ID 比较完整草稿；全局版本与路线编辑元数据不算配置变化，布局算变化。</summary>
internal static class ConfigurationDiff
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    public static ConfigurationChange[] Between(ConfigurationCheckpoint before, ConfigurationCheckpoint after)
    {
        var changes = new List<ConfigurationChange>();
        Compare("connection", before.Connections.Select(value => (value.Id, value.Key, (long?)null, Value: JsonSerializer.SerializeToElement(value, Json))),
            after.Connections.Select(value => (value.Id, value.Key, (long?)null, Value: JsonSerializer.SerializeToElement(value, Json))));
        Compare("device", before.Devices.Select(value => (value.Id, value.Key, (long?)null, Value: JsonSerializer.SerializeToElement(value, Json))),
            after.Devices.Select(value => (value.Id, value.Key, (long?)null, Value: JsonSerializer.SerializeToElement(value, Json))));
        Compare("input", before.Inputs.Select(value => (value.Id, value.Key, (long?)value.DeviceId, Value: JsonSerializer.SerializeToElement(value, Json))),
            after.Inputs.Select(value => (value.Id, value.Key, (long?)value.DeviceId, Value: JsonSerializer.SerializeToElement(value, Json))));
        Compare("route", before.Routes.Select(value => (value.Id, value.Key, (long?)null, Value: JsonSerializer.SerializeToElement(value with { UpdatedBy = null, UpdatedAt = 0 }, Json))),
            after.Routes.Select(value => (value.Id, value.Key, (long?)null, Value: JsonSerializer.SerializeToElement(value with { UpdatedBy = null, UpdatedAt = 0 }, Json))));
        return changes.ToArray();

        void Compare(string kind, IEnumerable<(long Id, string Key, long? DeviceId, JsonElement Value)> previous,
            IEnumerable<(long Id, string Key, long? DeviceId, JsonElement Value)> next)
        {
            var old = previous.ToDictionary(value => value.Id);
            var current = next.ToDictionary(value => value.Id);
            foreach (var id in old.Keys.Union(current.Keys).Order())
            {
                var existed = old.TryGetValue(id, out var left);
                var exists = current.TryGetValue(id, out var right);
                if (existed && exists && JsonElement.DeepEquals(left.Value, right.Value))
                { continue; }
                var display = exists ? right : left;
                changes.Add(new(kind, id, display.Key, display.DeviceId, !existed ? "added" : !exists ? "removed" : "modified",
                    existed ? left.Value : null, exists ? right.Value : null));
            }
        }
    }
}

internal sealed record ConfigurationChange(string Kind, long Id, string Key, long? DeviceId, string Change,
    JsonElement? Before, JsonElement? After);
