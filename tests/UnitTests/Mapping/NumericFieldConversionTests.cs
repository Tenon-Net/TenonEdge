using TenonEdge.Abstractions.Messages;
using TenonEdge.Runtime.Mapping;

namespace TenonEdge.UnitTests.Mapping;

public sealed class NumericFieldConversionTests
{
    [Fact]
    public void UnchangedIntegersAndFloat32KeepTheirRepresentation()
    {
        var signed = FieldValue.FromInt64(long.MaxValue);
        var unsigned = FieldValue.FromUInt64(ulong.MaxValue);
        var single = FieldValue.FromSingle(23.7f);
        Assert.Same(signed, new NumericFieldConversion(FieldType.Integer).Convert(signed).Value);
        Assert.Same(unsigned, new NumericFieldConversion(FieldType.Integer).Convert(unsigned).Value);
        Assert.Same(single, new NumericFieldConversion(FieldType.Number).Convert(single).Value);
        Assert.Equal("18446744073709551615", unsigned.ToString());
        Assert.Equal("23.7", single.ToString());
    }

    [Fact]
    public void IntegerArithmeticIsExactAndUnsignedExceptionEndsWhenCalculationIsConfigured()
    {
        var value = FieldValue.FromUInt64(ulong.MaxValue);
        var result = new NumericFieldConversion(FieldType.Number, scale: 0.1m).Convert(value);
        Assert.Null(result.Error);
        Assert.Equal("1844674407370955161.5", result.Value!.ToString());
        Assert.Equal(QualityCode.Overflow, new NumericFieldConversion(FieldType.Integer, scale: 1m).Convert(value).Error);
        Assert.Equal("9223372036854775806", new NumericFieldConversion(FieldType.Integer, offset: -1m)
            .Convert(FieldValue.FromInt64(long.MaxValue)).Value!.ToString());
        Assert.Equal("-30", new NumericFieldConversion(FieldType.Integer, offset: -40m)
            .Convert(FieldValue.FromUInt64(10)).Value!.ToString());
    }

    [Theory]
    [InlineData("2.5", "3")]
    [InlineData("-2.5", "-3")]
    [InlineData("2.4", "2")]
    public void DecimalIntegerRoundingIsExplicitAndAwayFromZero(string raw, string expected)
    {
        var value = FieldValue.FromDecimal(decimal.Parse(raw, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(QualityCode.ConversionFailed, new NumericFieldConversion(FieldType.Integer).Convert(value).Error);
        var result = new NumericFieldConversion(FieldType.Integer, decimalPlaces: 0).Convert(value);
        Assert.Null(result.Error);
        Assert.Equal(expected, result.Value!.ToString());
    }

    [Theory]
    [InlineData(2.5, "3")]
    [InlineData(-2.5, "-3")]
    public void FloatingIntegerRoundingIsAwayFromZero(double value, string expected)
    {
        var result = new NumericFieldConversion(FieldType.Integer, decimalPlaces: 0).Convert(FieldValue.FromDouble(value));
        Assert.Null(result.Error);
        Assert.Equal(expected, result.Value!.ToString());
    }

    [Fact]
    public void OverflowAndNonFiniteAreDistinctAndNeverSupplyAValue()
    {
        var exact = new NumericFieldConversion(FieldType.Number, scale: 2m).Convert(FieldValue.FromDecimal(decimal.MaxValue));
        var floating = new NumericFieldConversion(FieldType.Number, scale: 2m).Convert(FieldValue.FromDouble(double.MaxValue));
        Assert.Equal(QualityCode.Overflow, exact.Error);
        Assert.Equal(QualityCode.NonFinite, floating.Error);
        Assert.Null(exact.Value);
        Assert.Null(floating.Value);
    }

    [Theory]
    [InlineData(9223372036854775808d, true)]
    [InlineData(-9223372036854775808d, false)]
    [InlineData(-9223372036854777856d, true)]
    public void FloatingIntegerBoundaryDoesNotCompareAgainstRoundedLongMax(double value, bool overflow)
    {
        var result = new NumericFieldConversion(FieldType.Integer).Convert(FieldValue.FromDouble(value));
        Assert.Equal(overflow ? QualityCode.Overflow : null, result.Error);
        if (!overflow)
        { Assert.Equal(long.MinValue.ToString(System.Globalization.CultureInfo.InvariantCulture), result.Value!.ToString()); }
    }

    [Fact]
    public void PrecisionAboveFifteenWorksForSmallFloatingValuesWithoutOverflowingLargeOnes()
    {
        var conversion = new NumericFieldConversion(FieldType.Number, decimalPlaces: 20);
        Assert.Equal(FieldValue.FromDouble(1.23e-18), conversion.Convert(FieldValue.FromDouble(1.234e-18)).Value);
        Assert.Equal(FieldValue.FromDouble(double.MaxValue), conversion.Convert(FieldValue.FromDouble(double.MaxValue)).Value);
    }

    [Fact]
    public void DecimalPrecisionAndTargetTypeArePreserved()
    {
        var conversion = new NumericFieldConversion(FieldType.Number, scale: 0.1m, offset: -40m, decimalPlaces: 2);
        var result = conversion.Convert(FieldValue.FromInt64(423));
        Assert.Equal(FieldType.Number, result.Value!.Type);
        Assert.Equal("2.3", result.Value.ToString());
        Assert.Null(result.Error);
    }

    [Fact]
    public void BooleanAndStringAreNotImplicitNumbersAndFailedCallsDoNotReuseSuccess()
    {
        var conversion = new NumericFieldConversion(FieldType.Number);
        Assert.NotNull(conversion.Convert(FieldValue.FromInt64(1)).Value);
        Assert.Equal(QualityCode.ConversionFailed, conversion.Convert(FieldValue.FromBoolean(true)).Error);
        Assert.Null(conversion.Convert(FieldValue.FromString("1")).Value);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(29)]
    public void InvalidPrecisionIsAConfigurationError(int places) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new NumericFieldConversion(FieldType.Number, decimalPlaces: places));

    [Fact]
    public void ExtractionDoesNotConvertThroughAnInexactRepresentation()
    {
        Assert.True(FieldValue.FromUInt64(ulong.MaxValue).TryGetExactNumber(out var exact));
        Assert.Equal((decimal)ulong.MaxValue, exact);
        Assert.False(FieldValue.FromUInt64(ulong.MaxValue).TryGetFloatingNumber(out _));
        Assert.False(FieldValue.FromDouble(1.0).TryGetExactNumber(out _));
        Assert.True(FieldValue.FromSingle(23.7f).TryGetFloatingNumber(out var floating));
        Assert.Equal((double)23.7f, floating);
    }
}
