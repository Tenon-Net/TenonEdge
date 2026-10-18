using System.Globalization;
using System.Text.Json;

namespace TenonEdge.Abstractions.Parameters;

// JSON grammar is checked by System.Text.Json. Comparing normalized decimal digits avoids
// rounding just outside a bound, including integers above 2^53 and decimal underflow.
internal readonly record struct ParameterNumber(string Digits, int Power, bool Negative)
{
    public bool WholeNumber => Digits == "0" || Power >= 0;

    public static bool TryRead(JsonElement value, out ParameterNumber number)
    {
        number = default;
        if (value.ValueKind != JsonValueKind.Number)
        { return false; }
        var raw = value.GetRawText();
        if (raw.Length > 128)
        { return false; }
        var negative = raw[0] == '-';
        var parts = raw.TrimStart('-').Split(['e', 'E']);
        var exponent = 0;
        if (parts.Length == 2 && (!int.TryParse(parts[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out exponent) ||
            exponent is < -1000000 or > 1000000))
        { return false; }
        var dot = parts[0].IndexOf('.');
        var power = exponent - (dot < 0 ? 0 : parts[0].Length - dot - 1);
        var digits = parts[0].Replace(".", "", StringComparison.Ordinal).TrimStart('0');
        if (digits.Length == 0)
        { number = new("0", 0, false); return true; }
        var trimmed = digits.TrimEnd('0');
        number = new(trimmed, power + digits.Length - trimmed.Length, negative);
        return true;
    }

    public static ParameterNumber Read(string raw)
    {
        if (raw.Length > 128)
        { throw new ArgumentException("参数数值边界过长。"); }
        using var document = JsonDocument.Parse(raw);
        return TryRead(document.RootElement, out var number) ? number : throw new ArgumentException("参数数值边界无效。");
    }

    public int CompareTo(ParameterNumber other)
    {
        if (Negative != other.Negative)
        { return Negative ? -1 : 1; }
        var result = Digits == "0" ? (other.Digits == "0" ? 0 : -1) : other.Digits == "0" ? 1 :
            (Digits.Length + Power).CompareTo(other.Digits.Length + other.Power);
        if (result == 0 && Digits != "0")
        {
            var width = Math.Max(Digits.Length, other.Digits.Length);
            result = string.CompareOrdinal(Digits.PadRight(width, '0'), other.Digits.PadRight(width, '0'));
        }
        return Negative ? -result : result;
    }
}
