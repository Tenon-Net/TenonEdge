using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using TenonEdge.Abstractions.Messages;

namespace TenonEdge.Runtime.Mapping;

/// <summary>
/// 按字段声明读取一个 JSON 标量；整数始终精确按有符号 64 位处理。
/// number 只有能精确表示时才用 decimal，否则按有限 double；对象、数组和 null 不冒充字符串或零。
/// </summary>
public static partial class JsonFieldValueReader
{
    public static FieldConversionResult Read(JsonElement value, FieldType declaredType)
    {
        if (!Enum.IsDefined(declaredType))
        { throw new ArgumentOutOfRangeException(nameof(declaredType)); }
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined or JsonValueKind.Object or JsonValueKind.Array)
        { return Failed(QualityCode.ConversionFailed); }
        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return declaredType switch
            {
                FieldType.Boolean => new(FieldValue.FromBoolean(value.GetBoolean()), null),
                FieldType.String => new(FieldValue.FromString(value.GetBoolean() ? "true" : "false"), null),
                _ => Failed(QualityCode.ConversionFailed),
            };
        }
        var raw = value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();
        return ReadText(raw, declaredType, value.ValueKind == JsonValueKind.String);
    }

    internal static FieldConversionResult ReadText(string raw, FieldType declaredType, bool fromString = true)
    {
        if (declaredType == FieldType.String && fromString)
        { return new(FieldValue.FromString(raw), null); }
        if (declaredType == FieldType.Boolean && fromString)
        {
            return raw switch
            {
                "true" => new(FieldValue.FromBoolean(true), null),
                "false" => new(FieldValue.FromBoolean(false), null),
                _ => Failed(QualityCode.ConversionFailed),
            };
        }
        if (!TryNormalize(raw, out var number))
        { return Failed(QualityCode.ConversionFailed); }
        if (declaredType is FieldType.Integer or FieldType.Boolean)
        {
            var integer = ReadInteger(number);
            if (declaredType == FieldType.Integer)
            { return integer; }
            if (integer.Value is null)
            { return Failed(QualityCode.ConversionFailed); }
            integer.Value.TryGetExactNumber(out var exact);
            return exact is 0m or 1m ? new(FieldValue.FromBoolean(exact == 1m), null) : Failed(QualityCode.ConversionFailed);
        }
        var result = ReadNumber(raw, number);
        return declaredType == FieldType.String && result.Value is { } parsed
            ? new(FieldValue.FromString(parsed.ToString()), null) : result;
    }

    private static FieldConversionResult ReadInteger(NumberParts number)
    {
        if (number.Digits == "0")
        { return new(FieldValue.FromInt64(0), null); }
        if (number.Power < 0)
        { return Failed(QualityCode.ConversionFailed); }
        if (number.Digits.Length + number.Power > 19)
        { return Failed(QualityCode.Overflow); }
        // 长度已限制：最多 19 位，没有大指数分配或 double 中转。
        var text = (number.Negative ? "-" : "") + number.Digits + new string('0', (int)number.Power);
        return long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer)
            ? new(FieldValue.FromInt64(integer), null) : Failed(QualityCode.Overflow);
    }

    private static FieldConversionResult ReadNumber(string raw, NumberParts number)
    {
        if (number.Digits == "0")
        { return new(FieldValue.FromDecimal(0m), null); }
        if (number.Digits.Length <= 29 && number.Power is >= -28 and <= 28)
        {
            var text = (number.Negative ? "-" : "") + number.Digits + "E" + number.Power.ToString(CultureInfo.InvariantCulture);
            if (decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var exact) &&
                TryNormalize(exact.ToString(CultureInfo.InvariantCulture), out var roundTrip) && number == roundTrip)
            {
                return new(FieldValue.FromDecimal(exact), null);
            }
        }
        // TryParse(decimal) 成功也可能已经舍入。只有规范化后逐位一致才使用它。
        if (!double.TryParse(raw, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent,
            CultureInfo.InvariantCulture, out var approximate))
        { return Failed(QualityCode.ConversionFailed); }
        return FieldValue.TryFromDouble(approximate, out var result) ? new(result, null) : Failed(QualityCode.NonFinite);
    }

    private static bool TryNormalize(string text, out NumberParts number)
    {
        number = default;
        var match = NumericText().Match(text);
        if (!match.Success)
        { return false; }
        var fraction = match.Groups["fraction"].Value;
        var digits = (match.Groups["whole"].Value + fraction).TrimStart('0');
        if (digits.Length == 0)
        { number = new("0", 0, false); return true; }
        var exponentText = match.Groups["exponent"].Value;
        var exponent = 0;
        if (exponentText.Length != 0 && !int.TryParse(exponentText, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out exponent))
        {
            // 极端指数只用于判定超界，不据此构造大整数或巨大字符串。
            exponent = exponentText[0] == '-' ? int.MinValue : int.MaxValue;
        }
        var significant = digits.TrimEnd('0');
        number = new(significant, (long)exponent - fraction.Length + digits.Length - significant.Length,
            match.Groups["sign"].Value == "-");
        return true;
    }

    private readonly record struct NumberParts(string Digits, long Power, bool Negative);
    private static FieldConversionResult Failed(QualityCode error) => new(null, error);

    [GeneratedRegex(@"\A(?<sign>[+-]?)(?<whole>[0-9]+)(?:\.(?<fraction>[0-9]+))?(?:[eE](?<exponent>[+-]?[0-9]+))?\z", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex NumericText();
}
