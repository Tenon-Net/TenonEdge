using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Abstractions.Plans;
using TenonEdge.Adapters.Simulator;

namespace TenonEdge.Host.Features.Inputs;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record SimulatorSourceDraft(int PeriodMs = 5000, string? Template = null, string? SampleJson = null,
    double MissingField = 0, double InvalidValue = 0, double ConnectionError = 0);

internal static class SimulatorSourceValidation
{
    public static SimulatorSourceDraft Normalize(SimulatorSourceDraft source, string deviceKey, string inputKey)
    {
        if (source.Template is not null && source.SampleJson is not null ||
            source.SampleJson is { } raw && Encoding.UTF8.GetByteCount(raw) > 4 * 1024 * 1024)
        { throw new ArgumentException("模拟模板与自定义样本只能选一项，样本不能超过 4 MiB。"); }
        var settings = new Dictionary<string, object?>
        {
            ["periodMs"] = source.PeriodMs,
            ["faults"] = new { missingField = source.MissingField, invalidValue = source.InvalidValue, connectionError = source.ConnectionError },
        };
        if (source.SampleJson is { } sample)
        {
            using var document = JsonDocument.Parse(sample);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            { throw new ArgumentException("模拟样本须为一层对象。"); }
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!BusinessKeys.IsValid(property.Name) || !keys.Add(property.Name))
                { throw new ArgumentException("模拟样本字段 key 无效或重复。"); }
            }
            settings["sample"] = document.RootElement.Clone();
        }
        else
        { settings["template"] = source.Template ?? "meter"; }
        try
        {
            _ = SimulatorInputFactory.Describe(new RuntimeUnitPlan(
                RuntimeUnitId.Create(SimulatorInputFactory.UnitKind, deviceKey + "/" + inputKey),
                JsonSerializer.SerializeToElement(settings)), deviceKey, inputKey);
        }
        catch (RuntimeUnitBuildException) { throw new ArgumentException("模拟来源的周期、模板、字段值或故障比例无效。"); }
        // Keep custom JSON text: parsing in JavaScript would round 64-bit integers.
        return source with { Template = source.SampleJson is null ? source.Template ?? "meter" : null };
    }

    public static IEnumerable<string> FieldKeys(SimulatorSourceDraft source)
    {
        if (source.SampleJson is null)
        { return SimulatorInputFactory.MeterFields.Keys; }
        using var document = JsonDocument.Parse(source.SampleJson);
        return document.RootElement.EnumerateObject().Select(field => field.Name).ToArray();
    }
}
