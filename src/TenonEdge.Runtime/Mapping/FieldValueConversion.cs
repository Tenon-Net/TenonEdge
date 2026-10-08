using System.Text.Json;
using TenonEdge.Abstractions.Messages;

namespace TenonEdge.Runtime.Mapping;

/// <summary>显式标量类型转换与数值换算组合；配置固定类型，不根据值大小改变输出形式。</summary>
public sealed class FieldValueConversion
{
    private readonly FieldType _target;
    private readonly bool _calculate;
    private readonly NumericFieldConversion? _numeric;

    public FieldValueConversion(FieldType target, decimal? scale = null, decimal? offset = null, int? decimalPlaces = null)
    {
        if (!Enum.IsDefined(target))
        { throw new ArgumentOutOfRangeException(nameof(target)); }
        _target = target;
        _calculate = scale.HasValue || offset.HasValue || decimalPlaces.HasValue;
        if (target == FieldType.Boolean && _calculate)
        { throw new ArgumentException("布尔目标不能配置数值换算。", nameof(target)); }
        if (target is FieldType.Integer or FieldType.Number || _calculate)
        { _numeric = new(target == FieldType.String ? FieldType.Number : target, scale, offset, decimalPlaces); }
    }

    public FieldConversionResult Convert(JsonElement source)
    {
        // 声明为整数且不做换算时直接精确读取，不能先以 number/double 读取后再判断整数。
        var readType = !_calculate ? _target : source.ValueKind switch
        {
            JsonValueKind.String => FieldType.String,
            JsonValueKind.True or JsonValueKind.False => FieldType.Boolean,
            _ => FieldType.Number,
        };
        var read = JsonFieldValueReader.Read(source, readType);
        return read.Value is { } value ? Convert(value) : read;
    }

    public FieldConversionResult Convert(FieldValue source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (_target == FieldType.String && !_calculate)
        { return new(FieldValue.FromString(source.ToString()), null); }
        if (_target == FieldType.Boolean)
        {
            if (source.Type == FieldType.Boolean)
            { return new(source, null); }
            if (source.Type == FieldType.String)
            { return JsonFieldValueReader.ReadText(source.ToString(), FieldType.Boolean); }
            if (source.Type == FieldType.Integer && source.TryGetExactNumber(out var integer) && integer is 0m or 1m)
            { return new(FieldValue.FromBoolean(integer == 1m), null); }
            return new(null, QualityCode.ConversionFailed);
        }
        if (source.Type == FieldType.String)
        {
            var read = JsonFieldValueReader.ReadText(source.ToString(), _calculate ? FieldType.Number : _target);
            if (read.Value is null)
            { return read; }
            source = read.Value;
        }
        var result = _numeric!.Convert(source);
        return _target == FieldType.String && result.Value is { } value
            ? new(FieldValue.FromString(value.ToString()), null) : result;
    }
}
