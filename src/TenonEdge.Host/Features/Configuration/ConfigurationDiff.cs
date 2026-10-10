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
        var metadata = (after.Edits ?? []).ToDictionary(edit => (edit.Kind, edit.Id));
        // 旧路线已保存可靠的作者/时间；其他旧对象没有元数据时保持未知，不使用当前时间补造。
        foreach (var route in after.Routes)
        { metadata.TryAdd(("route", route.Id), new("route", route.Id, "", route.UpdatedBy, route.UpdatedAt > 0 ? route.UpdatedAt : null, false)); }

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
                if (existed && exists && Same(left.Value, right.Value))
                { continue; }
                var display = exists ? right : left;
                changes.Add(new(kind, id, display.Key, display.DeviceId, !existed ? "added" : !exists ? "removed" : "modified",
                    existed ? left.Value : null, exists ? right.Value : null,
                    metadata.GetValueOrDefault((kind, id))?.UpdatedBy, metadata.GetValueOrDefault((kind, id))?.UpdatedAt));
            }
        }
    }

    // 与运行计划的数字原文规则一致；1 与 1.0 不能在回滚前被误判为“草稿未改动”。
    private static bool Same(JsonElement left, JsonElement right)
    {
        if (left.ValueKind != right.ValueKind)
        { return false; }
        if (left.ValueKind == JsonValueKind.Number)
        { return left.GetRawText() == right.GetRawText(); }
        if (left.ValueKind == JsonValueKind.Object)
        {
            var properties = right.EnumerateObject().ToDictionary(property => property.Name, property => property.Value, StringComparer.Ordinal);
            foreach (var property in left.EnumerateObject())
            {
                if (!properties.Remove(property.Name, out var value) || !Same(property.Value, value))
                { return false; }
            }
            return properties.Count == 0;
        }
        if (left.ValueKind == JsonValueKind.Array)
        {
            if (left.GetArrayLength() != right.GetArrayLength())
            { return false; }
            var values = right.EnumerateArray();
            foreach (var value in left.EnumerateArray())
            {
                if (!values.MoveNext() || !Same(value, values.Current))
                { return false; }
            }
            return true;
        }
        return JsonElement.DeepEquals(left, right);
    }
}

internal sealed record ConfigurationChange(string Kind, long Id, string Key, long? DeviceId, string Change,
    JsonElement? Before, JsonElement? After, string? UpdatedBy = null, long? UpdatedAt = null);
