using TenonEdge.Abstractions.Messages;

namespace TenonEdge.Runtime.Mapping;

/// <summary>数值转换的单字段结果；失败原因供映射路径标记 quality，不能用旧值或零代替。</summary>
public sealed record FieldConversionResult(FieldValue? Value, QualityCode? Error);

/// <summary>
/// 已有数值的换算核心：整数/decimal 经 decimal，浮点经 double。JSON/字符串的解析由字段映射入口处理。
/// null 参数表示未配置；显式 scale=1 或 offset=0 仍是一次换算，不保留 uint64 的特殊范围。
/// </summary>
public sealed class NumericFieldConversion
{
    private readonly FieldType _target;
    private readonly decimal? _scale;
    private readonly decimal? _offset;
    private readonly int? _decimalPlaces;
    private bool HasCalculation => _scale.HasValue || _offset.HasValue || _decimalPlaces.HasValue;

    public NumericFieldConversion(FieldType target, decimal? scale = null, decimal? offset = null, int? decimalPlaces = null)
    {
        if (target is not (FieldType.Integer or FieldType.Number))
        { throw new ArgumentOutOfRangeException(nameof(target)); }
        if (decimalPlaces is < 0 or > 28)
        { throw new ArgumentOutOfRangeException(nameof(decimalPlaces)); }
        _target = target;
        _scale = scale;
        _offset = offset;
        _decimalPlaces = decimalPlaces;
    }

    public FieldConversionResult Convert(FieldValue source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Type is not (FieldType.Integer or FieldType.Number))
        { return Failed(QualityCode.ConversionFailed); }
        if (!HasCalculation && source.Type == _target)
        {
            // 不变换的 uint64 保留范围；float32 保留其最短单精度序列化。
            return new(source, null);
        }
        if (source.TryGetExactNumber(out var exact))
        { return ConvertExact(exact); }
        if (source.TryGetFloatingNumber(out var approximate))
        { return ConvertFloating(approximate); }
        return Failed(QualityCode.ConversionFailed);
    }

    private FieldConversionResult ConvertExact(decimal value)
    {
        try
        {
            value = checked(value * (_scale ?? 1m) + (_offset ?? 0m));
            if (_decimalPlaces is { } places)
            { value = decimal.Round(value, places, MidpointRounding.AwayFromZero); }
            if (_target == FieldType.Number)
            { return new(FieldValue.FromDecimal(value), null); }
            if (value != decimal.Truncate(value))
            { return Failed(QualityCode.ConversionFailed); }
            return new(FieldValue.FromInt64(checked((long)value)), null);
        }
        catch (OverflowException) { return Failed(QualityCode.Overflow); }
    }

    private FieldConversionResult ConvertFloating(double value)
    {
        value = value * (double)(_scale ?? 1m) + (double)(_offset ?? 0m);
        if (!double.IsFinite(value))
        { return Failed(QualityCode.NonFinite); }
        if (_decimalPlaces is { } places)
        { value = RoundFloating(value, places); }
        if (_target == FieldType.Number)
        {
            return FieldValue.TryFromDouble(value, out var result) ? new(result, null) : Failed(QualityCode.NonFinite);
        }
        if (value != Math.Truncate(value))
        { return Failed(QualityCode.ConversionFailed); }
        // checked 转换不可用 (double)long.MaxValue 作 <= 比较：那个边界已经舍入成 2^63。
        try
        { return new(FieldValue.FromInt64(checked((long)value)), null); }
        catch (OverflowException) { return Failed(QualityCode.Overflow); }
    }

    private static double RoundFloating(double value, int places)
    {
        if (places <= 15)
        { return Math.Round(value, places, MidpointRounding.AwayFromZero); }
        var factor = Math.Pow(10, places);
        // 超过有效精度时原值已经无法表示指定小数位，无需先乘一个可能溢出的因子。
        if (Math.Abs(value) >= 1e16 / factor)
        { return value; }
        return Math.Round(value * factor, MidpointRounding.AwayFromZero) / factor;
    }

    private static FieldConversionResult Failed(QualityCode error) => new(null, error);
}
