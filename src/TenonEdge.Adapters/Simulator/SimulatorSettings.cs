using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Abstractions.Plans;

namespace TenonEdge.Adapters.Simulator;

/// <summary>
/// 模拟输入的配置（功能需求 §3.5，GW-07）。解析与校验是纯函数，配置无效时抛出 <see cref="RuntimeUnitBuildException"/>：
/// <code>
/// {
///   "periodMs": 5000,                 // 周期，默认 5000，最小 1000（总设计 §8.4：采集周期首版最小 1 秒）
///   "seed": 42,                       // 可选；省略时由 deviceKey/inputKey 推出，同一数据来源总是同一序列
///   "template": "meter",              // 内置模板，与 sample 二选一；都省略时为 meter
///   "sample": { "temperature": 21.5 },// 自定义 JSON 样本：一层对象，值为数值、布尔或字符串，每周期原样发出
///   "faults": { "missingField": 0.1, "invalidValue": 0.05, "connectionError": 0.02 }  // 各类故障的比例，0—1
/// }
/// </code>
/// 未知的属性视为配置错误，避免拼写错误被悄悄忽略。
/// </summary>
internal sealed record SimulatorSettings(
    TimeSpan Period,
    ulong Seed,
    string? Template,
    ImmutableSortedDictionary<string, FieldValue>? Sample,
    double MissingFieldRatio,
    double InvalidValueRatio,
    double ConnectionErrorRatio)
{
    public const string MeterTemplate = "meter";

    public static readonly TimeSpan DefaultPeriod = TimeSpan.FromSeconds(5);

    public static readonly TimeSpan MinimumPeriod = TimeSpan.FromSeconds(1);

    public static readonly TimeSpan MaximumPeriod = TimeSpan.FromDays(1);

    public const int MaximumSampleFields = 1000;

    public const int MaximumFieldNameLength = 64;

    private static readonly HashSet<string> KnownProperties = new(StringComparer.Ordinal) { "periodMs", "seed", "template", "sample", "faults" };

    private static readonly HashSet<string> KnownFaults = new(StringComparer.Ordinal) { "missingField", "invalidValue", "connectionError" };

    public static SimulatorSettings Parse(RuntimeUnitPlan unit, string deviceKey, string inputKey)
    {
        ArgumentNullException.ThrowIfNull(unit);
        var configuration = unit.Configuration;
        foreach (var property in configuration.EnumerateObject())
        {
            if (!KnownProperties.Contains(property.Name))
            {
                throw Invalid(unit, $"未知的配置项 {property.Name}。");
            }
        }

        var period = DefaultPeriod;
        if (configuration.TryGetProperty("periodMs", out var periodElement))
        {
            if (periodElement.ValueKind != JsonValueKind.Number || !periodElement.TryGetInt64(out var milliseconds)
                || milliseconds < MinimumPeriod.TotalMilliseconds || milliseconds > MaximumPeriod.TotalMilliseconds)
            {
                throw Invalid(unit, $"periodMs 应为 {MinimumPeriod.TotalMilliseconds} 到 {MaximumPeriod.TotalMilliseconds} 之间的整数（毫秒）。");
            }

            period = TimeSpan.FromMilliseconds(milliseconds);
        }

        ulong seed;
        if (configuration.TryGetProperty("seed", out var seedElement))
        {
            if (seedElement.ValueKind != JsonValueKind.Number || !seedElement.TryGetInt64(out var value))
            {
                throw Invalid(unit, "seed 应为 64 位整数。");
            }

            seed = unchecked((ulong)value);
        }
        else
        {
            seed = BinaryPrimitives.ReadUInt64LittleEndian(SHA256.HashData(Encoding.UTF8.GetBytes($"{deviceKey}/{inputKey}")));
        }

        var hasTemplate = configuration.TryGetProperty("template", out var templateElement);
        var hasSample = configuration.TryGetProperty("sample", out var sampleElement);
        if (hasTemplate && hasSample)
        {
            throw Invalid(unit, "template 与 sample 只能选一个。");
        }

        string? template = null;
        ImmutableSortedDictionary<string, FieldValue>? sample = null;
        if (hasSample)
        {
            sample = ParseSample(unit, sampleElement);
        }
        else
        {
            template = !hasTemplate ? MeterTemplate
                : templateElement.ValueKind == JsonValueKind.String ? templateElement.GetString()
                : null;
            if (template != MeterTemplate)
            {
                throw Invalid(unit, $"template 只支持 {MeterTemplate}。");
            }
        }

        var (missing, invalid, connection) = ParseFaults(unit, configuration);
        return new SimulatorSettings(period, seed, template, sample, missing, invalid, connection);
    }

    private static ImmutableSortedDictionary<string, FieldValue> ParseSample(RuntimeUnitPlan unit, JsonElement sample)
    {
        if (sample.ValueKind != JsonValueKind.Object)
        {
            throw Invalid(unit, "sample 应为 JSON 对象。");
        }

        var builder = ImmutableSortedDictionary.CreateBuilder<string, FieldValue>(StringComparer.Ordinal);
        foreach (var field in sample.EnumerateObject())
        {
            if (field.Name.Length is 0 or > MaximumFieldNameLength || field.Name.Any(char.IsControl))
            {
                throw Invalid(unit, $"sample 的字段名应为 1 到 {MaximumFieldNameLength} 个字符，且不含控制字符。");
            }

            builder[field.Name] = field.Value.ValueKind switch
            {
                JsonValueKind.True => FieldValue.FromBoolean(true),
                JsonValueKind.False => FieldValue.FromBoolean(false),
                JsonValueKind.String => FieldValue.FromString(field.Value.GetString()!),
                JsonValueKind.Number => ParseNumber(unit, field.Name, field.Value),
                _ => throw Invalid(unit, $"sample 的字段 {field.Name} 应为数值、布尔或字符串：样本只支持一层字段，不支持空值。"),
            };
        }

        if (builder.Count is 0 or > MaximumSampleFields)
        {
            throw Invalid(unit, $"sample 应有 1 到 {MaximumSampleFields} 个字段。");
        }

        return builder.ToImmutable();
    }

    /// <summary>整数字面量按有符号 64 位整数，其余能用 decimal 精确表示的按 decimal，超出 decimal 范围的按 double（总设计 §6.4）。</summary>
    private static FieldValue ParseNumber(RuntimeUnitPlan unit, string name, JsonElement number)
    {
        var text = number.GetRawText();
        if (!text.Contains('.', StringComparison.Ordinal) && !text.Contains('e', StringComparison.OrdinalIgnoreCase) && number.TryGetInt64(out var integer))
        {
            return FieldValue.FromInt64(integer);
        }

        if (number.TryGetDecimal(out var value))
        {
            return FieldValue.FromDecimal(value);
        }

        if (number.TryGetDouble(out var floating) && FieldValue.TryFromDouble(floating, out var field))
        {
            return field;
        }

        throw Invalid(unit, $"sample 的字段 {name} 的数值超出可表示的范围。");
    }

    private static (double Missing, double Invalid, double Connection) ParseFaults(RuntimeUnitPlan unit, JsonElement configuration)
    {
        if (!configuration.TryGetProperty("faults", out var faults))
        {
            return (0, 0, 0);
        }

        if (faults.ValueKind != JsonValueKind.Object)
        {
            throw Invalid(unit, "faults 应为 JSON 对象。");
        }

        foreach (var property in faults.EnumerateObject())
        {
            if (!KnownFaults.Contains(property.Name))
            {
                throw Invalid(unit, $"未知的故障类型 {property.Name}。");
            }
        }

        return (Ratio(unit, faults, "missingField"), Ratio(unit, faults, "invalidValue"), Ratio(unit, faults, "connectionError"));
    }

    private static double Ratio(RuntimeUnitPlan unit, JsonElement faults, string name)
    {
        if (!faults.TryGetProperty(name, out var element))
        {
            return 0;
        }

        if (element.ValueKind != JsonValueKind.Number || !element.TryGetDouble(out var ratio) || double.IsNaN(ratio) || ratio is < 0 or > 1)
        {
            throw Invalid(unit, $"faults.{name} 应为 0 到 1 之间的数。");
        }

        return ratio;
    }

    private static RuntimeUnitBuildException Invalid(RuntimeUnitPlan unit, string message) => new(unit.Id, message);
}
