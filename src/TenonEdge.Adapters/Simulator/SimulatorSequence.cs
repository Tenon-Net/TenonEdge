using System.Collections.Immutable;
using TenonEdge.Abstractions.Messages;

namespace TenonEdge.Adapters.Simulator;

/// <summary>
/// 模拟输入每个周期的读取结果（功能需求 §3.5，GW-07），只由配置与种子决定：同一配置、同一种子得到同一序列，与运行时间、通道是否满无关。
/// <list type="bullet">
/// <item>电表模板：电压 214.0—226.0 V、电流 8.00—12.00 A、频率 49.90—50.10 Hz 随机波动；功率（kW）由电压与电流算出，
/// 电能（kWh）按功率与周期累加。数值全部由整数步长换算为 decimal，逐位确定。</item>
/// <item>自定义样本：每周期原样发出。</item>
/// <item>故障按比例模拟：连接错误时整组读取失败，不生成消息；非法值使一个数值字段读到 NaN，按总设计 §6.4 从值中省略并标为 bad（nonFinite）；
/// 缺字段使一个字段不出现，既不在值中也不在质量中（“未携带”）。</item>
/// </list>
/// 每次读取消耗固定个数的随机数（连接错误的那次也一样），因此第 k 次读取的结果只取决于 k；
/// 落后时被跳过的周期不读取，序列也不前进。
/// </summary>
internal sealed class SimulatorSequence
{
    private const string Voltage = "voltage";
    private const string Current = "current";
    private const string Power = "power";
    private const string Frequency = "frequency";
    private const string Energy = "energy";

    internal static IReadOnlyDictionary<string, FieldType> MeterFields { get; } =
        new Dictionary<string, FieldType>
        {
            [Voltage] = FieldType.Number,
            [Current] = FieldType.Number,
            [Power] = FieldType.Number,
            [Frequency] = FieldType.Number,
            [Energy] = FieldType.Number,
        }.ToImmutableDictionary(StringComparer.Ordinal);

    private readonly SimulatorSettings _settings;
    private readonly decimal _periodHours;
    private SplitMix64 _random;
    private decimal _energy;

    public SimulatorSequence(SimulatorSettings settings)
    {
        _settings = settings;
        _random = new SplitMix64(settings.Seed);
        _periodHours = (decimal)settings.Period.TotalMilliseconds / 3_600_000m;
        _energy = _random.NextInt32(100_000);
    }

    public SimulatedReading Next()
    {
        var connectionError = _random.NextDouble() < _settings.ConnectionErrorRatio;
        var missing = _random.NextDouble() < _settings.MissingFieldRatio;
        var missingIndex = _random.NextDouble();
        var invalid = _random.NextDouble() < _settings.InvalidValueRatio;
        var invalidIndex = _random.NextDouble();
        var values = _settings.Sample ?? NextMeterReading();
        if (connectionError)
        {
            return SimulatedReading.ConnectionError;
        }

        var builder = values.ToBuilder();
        var quality = ImmutableSortedDictionary.CreateBuilder<string, QualityCode>(StringComparer.Ordinal);
        if (invalid)
        {
            var numeric = builder.Where(field => field.Value.Type is FieldType.Number or FieldType.Integer).Select(field => field.Key).ToList();
            if (numeric.Count > 0)
            {
                var field = numeric[(int)(invalidIndex * numeric.Count)];
                builder.Remove(field);
                quality[field] = QualityCode.NonFinite;
            }
        }

        if (missing && builder.Count > 0)
        {
            builder.Remove(builder.Keys.ElementAt((int)(missingIndex * builder.Count)));
        }

        return new SimulatedReading(false, builder.ToImmutable(), quality.ToImmutable());
    }

    private ImmutableSortedDictionary<string, FieldValue> NextMeterReading()
    {
        var voltage = (2140 + _random.NextInt32(121)) / 10m;
        var current = (800 + _random.NextInt32(401)) / 100m;
        var frequency = (4990 + _random.NextInt32(21)) / 100m;
        var power = Math.Round(voltage * current / 1000m, 3, MidpointRounding.AwayFromZero);
        _energy += power * _periodHours;
        return ImmutableSortedDictionary.CreateRange(StringComparer.Ordinal, new Dictionary<string, FieldValue>
        {
            [Voltage] = FieldValue.FromDecimal(voltage),
            [Current] = FieldValue.FromDecimal(current),
            [Power] = FieldValue.FromDecimal(power),
            [Frequency] = FieldValue.FromDecimal(frequency),
            [Energy] = FieldValue.FromDecimal(Math.Round(_energy, 3, MidpointRounding.AwayFromZero)),
        });
    }
}

/// <summary>一个周期的模拟读取结果。</summary>
internal sealed record SimulatedReading(
    bool IsConnectionError,
    ImmutableSortedDictionary<string, FieldValue> Values,
    ImmutableSortedDictionary<string, QualityCode> Quality)
{
    public static SimulatedReading ConnectionError { get; } = new(
        true,
        ImmutableSortedDictionary<string, FieldValue>.Empty.WithComparers(StringComparer.Ordinal),
        ImmutableSortedDictionary<string, QualityCode>.Empty.WithComparers(StringComparer.Ordinal));
}
