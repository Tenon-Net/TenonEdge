using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using TenonEdge.Abstractions.Messages;

namespace TenonEdge.UnitTests.Abstractions;

/// <summary>
/// JSON 数值的序列化（V-12，T0-10；总设计 §6.4“数值与类型”）：long、ulong、decimal 逐位写出，decimal 不用指数；
/// float32 按单精度、double 按双精度的最短往返格式写出；NaN 与无穷不能成为字段值，因此不会写入 JSON。
/// 文本与地区设置无关；同一类型的字段在不同数值下 JSON 形式不变。
/// </summary>
public sealed class FieldValueTests
{
    [Theory]
    [InlineData(0L, "0")]
    [InlineData(-1L, "-1")]
    [InlineData(9_007_199_254_740_993L, "9007199254740993")] // 2^53 + 1：按双精度解析会变成 9007199254740992
    [InlineData(-9_007_199_254_740_993L, "-9007199254740993")]
    [InlineData(long.MaxValue, "9223372036854775807")]
    [InlineData(long.MinValue, "-9223372036854775808")]
    public void SignedIntegersAreWrittenDigitForDigit(long value, string expected)
    {
        var field = FieldValue.FromInt64(value);

        Assert.Equal(FieldType.Integer, field.Type);
        Assert.Equal(expected, Json(field));
        Assert.Equal(expected, field.ToString());
        using var document = JsonDocument.Parse(Json(field));
        Assert.Equal(value, document.RootElement.GetInt64());
    }

    [Theory]
    [InlineData(0UL, "0")]
    [InlineData(9_223_372_036_854_775_808UL, "9223372036854775808")] // 2^63：超出有符号 64 位
    [InlineData(ulong.MaxValue, "18446744073709551615")]
    public void UnsignedRegisterValuesKeepTheFullUnsignedRange(ulong value, string expected)
    {
        var field = FieldValue.FromUInt64(value);

        Assert.Equal(FieldType.Integer, field.Type);
        Assert.Equal(expected, Json(field));
        using var document = JsonDocument.Parse(Json(field));
        Assert.Equal(value, document.RootElement.GetUInt64());
    }

    [Theory]
    [InlineData("79228162514264337593543950335")] // decimal 最大值
    [InlineData("-79228162514264337593543950335")]
    [InlineData("123456789012345678.9012345678")] // 28 位有效数字
    [InlineData("0.0000000000000000000000000001")] // 最小的正数：仍不用指数
    [InlineData("1.50")] // 保留小数末尾的 0
    [InlineData("-40")]
    public void DecimalsAreWrittenDigitForDigitWithoutExponent(string text)
    {
        var value = decimal.Parse(text, NumberStyles.Number, CultureInfo.InvariantCulture);
        var field = FieldValue.FromDecimal(value);

        Assert.Equal(FieldType.Number, field.Type);
        Assert.Equal(text, Json(field));
        Assert.Equal(text, field.ToString());
        using var document = JsonDocument.Parse(Json(field));
        Assert.Equal(value, document.RootElement.GetDecimal());
    }

    [Theory]
    [InlineData(23.7f, "23.7")] // 按双精度写出会是 23.700000762939453
    [InlineData(-23.7f, "-23.7")]
    [InlineData(0.1f, "0.1")]
    [InlineData(float.MaxValue, "3.4028235E+38")]
    [InlineData(float.Epsilon, "1E-45")]
    public void Float32RegistersUseTheShortestSinglePrecisionText(float value, string expected)
    {
        var field = FieldValue.FromSingle(value);

        Assert.Equal(FieldType.Number, field.Type);
        Assert.Equal(expected, Json(field));
        Assert.Equal(
            BitConverter.SingleToInt32Bits(value),
            BitConverter.SingleToInt32Bits(float.Parse(Json(field), NumberStyles.Float, CultureInfo.InvariantCulture)));
    }

    [Theory]
    [InlineData(23.7, "23.7")]
    [InlineData(0.30000000000000004, "0.30000000000000004")] // 0.1 + 0.2
    [InlineData(1e21, "1E+21")]
    [InlineData(double.MaxValue, "1.7976931348623157E+308")]
    [InlineData(double.Epsilon, "5E-324")]
    public void DoublesUseTheShortestRoundTripText(double value, string expected)
    {
        var field = FieldValue.FromDouble(value);

        Assert.Equal(FieldType.Number, field.Type);
        Assert.Equal(expected, Json(field));
        Assert.Equal(
            BitConverter.DoubleToInt64Bits(value),
            BitConverter.DoubleToInt64Bits(double.Parse(Json(field), NumberStyles.Float, CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void SingleAndDoubleWritingOfTheSameRegisterValueDiffer()
    {
        // 同一个 float32 寄存器值换成 double 后写出会带上单精度的误差，这正是未经换算的 float32 要按单精度写出的原因。
        Assert.Equal("23.7", FieldValue.FromSingle(23.7f).ToString());
        Assert.Equal("23.700000762939453", FieldValue.FromDouble(23.7f).ToString());
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void NonFiniteDoublesCannotBecomeFieldValues(double value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => FieldValue.FromDouble(value));
        Assert.False(FieldValue.TryFromDouble(value, out var field));
        Assert.Null(field);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void NonFiniteSinglesCannotBecomeFieldValues(float value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => FieldValue.FromSingle(value));
        Assert.False(FieldValue.TryFromSingle(value, out var field));
        Assert.Null(field);
    }

    [Fact]
    public void IntegerFieldsKeepTheSameJsonFormAtEveryMagnitude()
    {
        // 同一字段不随数值大小在数字和字符串之间切换，也不出现指数或小数点。
        for (long value = 1, step = 0; step < 19; value *= 10, step++)
        {
            foreach (var candidate in new[] { value, -value, value + 1 })
            {
                var json = Json(FieldValue.FromInt64(candidate));
                var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(json));
                Assert.True(reader.Read());
                Assert.Equal(JsonTokenType.Number, reader.TokenType);
                Assert.Matches("^-?[0-9]+$", json);
            }
        }
    }

    [Fact]
    public void TextDoesNotDependOnTheCurrentCulture()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            // 德语地区用逗号作小数点、用句点分组。
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");

            Assert.Equal("23.7", FieldValue.FromSingle(23.7f).ToString());
            Assert.Equal("23.7", FieldValue.FromDouble(23.7).ToString());
            Assert.Equal("1234567.50", FieldValue.FromDecimal(1234567.50m).ToString());
            Assert.Equal("-1234567", FieldValue.FromInt64(-1234567).ToString());
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void BooleansAndStringsUseTheirJsonForms()
    {
        Assert.Equal("true", Json(FieldValue.FromBoolean(true)));
        Assert.Equal("false", FieldValue.FromBoolean(false).ToString());
        Assert.Equal(FieldType.Boolean, FieldValue.FromBoolean(true).Type);

        var text = FieldValue.FromString("9007199254740993");
        Assert.Equal(FieldType.String, text.Type);
        Assert.Equal("\"9007199254740993\"", Json(text));
        Assert.Equal("9007199254740993", text.ToString());
        Assert.Throws<ArgumentNullException>(() => FieldValue.FromString(null!));
    }

    [Fact]
    public void ValuesAreEqualOnlyWhenRepresentationAndTextMatch()
    {
        Assert.Equal(FieldValue.FromInt64(5), FieldValue.FromInt64(5));
        Assert.Equal(FieldValue.FromInt64(5).GetHashCode(), FieldValue.FromInt64(5).GetHashCode());
        Assert.Equal(FieldValue.FromDecimal(1.50m), FieldValue.FromDecimal(1.50m));

        // decimal 自身的相等比较认为 1.5 与 1.50 相等，但两者写出的文本不同。
        Assert.NotEqual(FieldValue.FromDecimal(1.5m), FieldValue.FromDecimal(1.50m));
        Assert.NotEqual(FieldValue.FromDouble(0.0), FieldValue.FromDouble(-0.0));
        Assert.NotEqual(FieldValue.FromSingle(23.7f), FieldValue.FromDouble(23.7));
        Assert.NotEqual(FieldValue.FromInt64(1), FieldValue.FromUInt64(1));
        Assert.NotEqual(FieldValue.FromString("1"), FieldValue.FromInt64(1));
    }

    [Fact]
    public void DecimalNegativeZeroIsStoredAsZero()
    {
        var negativeZero = decimal.Negate(0.0m);
        Assert.True(decimal.IsNegative(negativeZero));

        // 负零与零写出相同的文本，按零保存，相等比较与文本一致。
        Assert.Equal("0.0", FieldValue.FromDecimal(negativeZero).ToString());
        Assert.Equal(FieldValue.FromDecimal(0.0m), FieldValue.FromDecimal(negativeZero));
    }

    private static string Json(FieldValue value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            value.WriteTo(writer);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
