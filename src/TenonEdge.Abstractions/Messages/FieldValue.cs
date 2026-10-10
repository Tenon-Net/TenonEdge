using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;

namespace TenonEdge.Abstractions.Messages;

/// <summary>
/// 输出字段的值（总设计 §6.4“数值与类型”）。数值的内部表示决定写出的 JSON 文本：
/// <list type="bullet">
/// <item>integer：有符号 64 位整数逐位写出，不经过 double；只有未经换算的 uint64 寄存器用 <see cref="FromUInt64"/> 保留无符号 64 位范围。</item>
/// <item>number：decimal 逐位写出，不用指数，保留小数末尾的 0；未经换算的 float32 按单精度、其余浮点按双精度的最短往返格式写出，
/// 很大或很小的浮点值可能带指数（如 1E+21）。</item>
/// <item>NaN 与无穷不能成为字段值：创建时拒绝，由调用方把该字段判为 <see cref="QualityCode.NonFinite"/>，因此 JSON 中不会出现 NaN、Infinity。</item>
/// </list>
/// 文本与地区设置无关。值不可变，可以在多条路径之间共享；两个值相等，当且仅当内部表示相同且写出的文本相同。
/// </summary>
public sealed class FieldValue : IEquatable<FieldValue>
{
    private const string NonFiniteMessage = "NaN 与无穷不能作为字段值，应把该字段判为 nonFinite。";

    private static readonly FieldValue True = new(Representation.Boolean, bits: 1);
    private static readonly FieldValue False = new(Representation.Boolean, bits: 0);

    private readonly Representation _representation;

    // 布尔存 0 或 1，Int64 存数值本身，UInt64、Double、Single 存位模式（相等比较因此区分 0 与 -0）。
    private readonly long _bits;
    private readonly decimal _decimal;
    private readonly string? _text;

    private FieldValue(Representation representation, long bits = 0, decimal decimalValue = 0m, string? text = null)
    {
        _representation = representation;
        _bits = bits;
        _decimal = decimalValue;
        _text = text;
    }

    private enum Representation : byte
    {
        Boolean,
        Int64,
        UInt64,
        Decimal,
        Double,
        Single,
        String,
    }

    /// <summary>输出类型：四种之一。</summary>
    public FieldType Type => _representation switch
    {
        Representation.Boolean => FieldType.Boolean,
        Representation.Int64 or Representation.UInt64 => FieldType.Integer,
        Representation.Decimal or Representation.Double or Representation.Single => FieldType.Number,
        _ => FieldType.String,
    };

    /// <summary>在途预算的保守逻辑大小，包含值对象及其 UTF-16 文本；不是 CLR 堆测量。</summary>
    public long BudgetBytes => 64L + (_text is null ? 0 : 24L + 2L * _text.Length);

    public static FieldValue FromBoolean(bool value) => value ? True : False;

    /// <summary>integer：有符号 64 位整数。</summary>
    public static FieldValue FromInt64(long value) => new(Representation.Int64, bits: value);

    /// <summary>integer：只用于未经换算的 uint64 寄存器，保留无符号 64 位范围。</summary>
    public static FieldValue FromUInt64(ulong value) => new(Representation.UInt64, bits: unchecked((long)value));

    /// <summary>
    /// number：整数寄存器经比例或偏移换算的结果，以及能用 decimal 精确表示的 JSON 数。
    /// decimal 的负零与零写出相同的文本（例如 0.0），按零保存，相等比较因此仍与文本一致。
    /// </summary>
    public static FieldValue FromDecimal(decimal value) =>
        new(Representation.Decimal, decimalValue: value == 0m && decimal.IsNegative(value) ? decimal.Negate(value) : value);

    /// <summary>number：浮点寄存器及其换算结果，按双精度写出。NaN 与无穷抛出 <see cref="ArgumentOutOfRangeException"/>。</summary>
    public static FieldValue FromDouble(double value) =>
        TryFromDouble(value, out var fieldValue)
            ? fieldValue
            : throw new ArgumentOutOfRangeException(nameof(value), value, NonFiniteMessage);

    /// <summary>与 <see cref="FromDouble"/> 相同，但对 NaN 与无穷返回 false，调用方据此把字段判为 nonFinite。</summary>
    public static bool TryFromDouble(double value, [NotNullWhen(true)] out FieldValue? fieldValue)
    {
        fieldValue = double.IsFinite(value) ? new FieldValue(Representation.Double, bits: BitConverter.DoubleToInt64Bits(value)) : null;
        return fieldValue is not null;
    }

    /// <summary>number：未经换算的 float32 寄存器，按单精度写出（例如 23.7 写为 23.7）。NaN 与无穷抛出 <see cref="ArgumentOutOfRangeException"/>。</summary>
    public static FieldValue FromSingle(float value) =>
        TryFromSingle(value, out var fieldValue)
            ? fieldValue
            : throw new ArgumentOutOfRangeException(nameof(value), value, NonFiniteMessage);

    /// <summary>与 <see cref="FromSingle"/> 相同，但对 NaN 与无穷返回 false，调用方据此把字段判为 nonFinite。</summary>
    public static bool TryFromSingle(float value, [NotNullWhen(true)] out FieldValue? fieldValue)
    {
        fieldValue = float.IsFinite(value) ? new FieldValue(Representation.Single, bits: BitConverter.SingleToInt32Bits(value)) : null;
        return fieldValue is not null;
    }

    public static FieldValue FromString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new FieldValue(Representation.String, text: value);
    }

    /// <summary>仅提取整数和 decimal 的精确数值，不把浮点或字符串隐式转换为 decimal。</summary>
    public bool TryGetExactNumber(out decimal value)
    {
        value = _representation switch
        {
            Representation.Int64 => _bits,
            Representation.UInt64 => unchecked((ulong)_bits),
            Representation.Decimal => _decimal,
            _ => default,
        };
        return _representation is Representation.Int64 or Representation.UInt64 or Representation.Decimal;
    }

    /// <summary>仅提取原始浮点表示；float32 精确提升为 double，整数不会经过此入口丢失精度。</summary>
    public bool TryGetFloatingNumber(out double value)
    {
        value = _representation switch
        {
            Representation.Single => BitConverter.Int32BitsToSingle(unchecked((int)_bits)),
            Representation.Double => BitConverter.Int64BitsToDouble(_bits),
            _ => default,
        };
        return _representation is Representation.Single or Representation.Double;
    }

    /// <summary>写出 JSON 值。</summary>
    public void WriteTo(Utf8JsonWriter writer) => WriteValue(writer, flushSegments: false);

    private void WriteValue(Utf8JsonWriter writer, bool flushSegments)
    {
        ArgumentNullException.ThrowIfNull(writer);
        switch (_representation)
        {
            case Representation.Boolean:
                writer.WriteBooleanValue(_bits != 0);
                break;
            case Representation.Int64:
                writer.WriteNumberValue(_bits);
                break;
            case Representation.UInt64:
                writer.WriteNumberValue(unchecked((ulong)_bits));
                break;
            case Representation.Decimal:
                writer.WriteNumberValue(_decimal);
                break;
            case Representation.Double:
                writer.WriteNumberValue(BitConverter.Int64BitsToDouble(_bits));
                break;
            case Representation.Single:
                writer.WriteNumberValue(BitConverter.Int32BitsToSingle(unchecked((int)_bits)));
                break;
            default:
                // .NET 10 分段字符串 API 保留跨段代理对，避免按整个长字符串申请转义暂存。
                var remaining = _text.AsSpan();
                const int segmentLength = 512;
                while (remaining.Length > segmentLength)
                {
                    writer.WriteStringValueSegment(remaining[..segmentLength], isFinalSegment: false);
                    remaining = remaining[segmentLength..];
                    if (flushSegments)
                    { writer.Flush(); }
                }

                writer.WriteStringValueSegment(remaining, isFinalSegment: true);
                break;
        }
    }

    /// <summary>
    /// 写出诊断快照 [内部表示, 值]，不同于业务 JSON。显式类型标签保证整数符号、
    /// decimal 小数位数及 float32/float64 在重放时不被 JSON 推断改写。
    /// </summary>
    public void WriteSnapshot(Utf8JsonWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStartArray();
        writer.WriteStringValue(_representation switch
        {
            Representation.Boolean => "boolean",
            Representation.Int64 => "int64",
            Representation.UInt64 => "uint64",
            Representation.Decimal => "decimal",
            Representation.Double => "float64",
            Representation.Single => "float32",
            _ => "string",
        });
        WriteValue(writer, flushSegments: true);
        writer.WriteEndArray();
    }

    /// <summary>读取内部诊断快照；不猜测类型，不接受非有限浮点或未知标签。</summary>
    public static FieldValue ReadSnapshot(JsonElement snapshot)
    {
        if (snapshot.ValueKind != JsonValueKind.Array || snapshot.GetArrayLength() != 2 ||
            snapshot[0].ValueKind != JsonValueKind.String)
        { throw new JsonException("字段快照格式无效。"); }
        var value = snapshot[1];
        try
        {
            return snapshot[0].GetString() switch
            {
                "boolean" => FromBoolean(value.GetBoolean()),
                "int64" => FromInt64(value.GetInt64()),
                "uint64" => FromUInt64(value.GetUInt64()),
                "decimal" => FromDecimal(value.GetDecimal()),
                "float64" => FromDouble(value.GetDouble()),
                "float32" => FromSingle(value.GetSingle()),
                "string" => FromString(value.GetString() ?? throw new JsonException("字段快照不能为 null。")),
                _ => throw new JsonException("未知的字段快照表示。"),
            };
        }
        catch (Exception exception) when (exception is InvalidOperationException or FormatException or ArgumentException)
        { throw new JsonException("字段快照值无效。"); }
    }

    /// <summary>
    /// 转为文本（总设计 §6.4“转为 string”）：数值取写出 JSON 时的同一段文本，布尔为 true 或 false，字符串为原文。
    /// 管理 API 返回点位值时以这段文本附类型返回（总设计 §10.1）。
    /// </summary>
    public override string ToString() => _representation switch
    {
        Representation.Boolean => _bits != 0 ? "true" : "false",
        Representation.String => _text!,
        _ => FormatNumber(),
    };

    public bool Equals(FieldValue? other)
    {
        if (other is null)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        // decimal 的相等比较忽略小数位数（1.5 与 1.50 相等），这里连同位数一起比较，与写出的文本一致。
        Span<int> bits = stackalloc int[4];
        Span<int> otherBits = stackalloc int[4];
        decimal.GetBits(_decimal, bits);
        decimal.GetBits(other._decimal, otherBits);
        return _representation == other._representation
            && _bits == other._bits
            && bits.SequenceEqual(otherBits)
            && string.Equals(_text, other._text, StringComparison.Ordinal);
    }

    public override bool Equals(object? obj) => Equals(obj as FieldValue);

    public override int GetHashCode()
    {
        Span<int> bits = stackalloc int[4];
        decimal.GetBits(_decimal, bits);
        return HashCode.Combine(
            _representation,
            _bits,
            HashCode.Combine(bits[0], bits[1], bits[2], bits[3]),
            _text is null ? 0 : StringComparer.Ordinal.GetHashCode(_text));
    }

    private string FormatNumber()
    {
        var buffer = new ArrayBufferWriter<byte>(64);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteTo(writer);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
