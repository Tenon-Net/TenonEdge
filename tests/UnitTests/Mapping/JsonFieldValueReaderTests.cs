using System.Globalization;
using System.Text.Json;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Runtime.Mapping;

namespace TenonEdge.UnitTests.Mapping;

public sealed class JsonFieldValueReaderTests
{
    [Theory]
    [InlineData("9223372036854775807", "9223372036854775807")]
    [InlineData("-9223372036854775808", "-9223372036854775808")]
    [InlineData("9007199254740993", "9007199254740993")]
    [InlineData("9.007199254740993e15", "9007199254740993")]
    [InlineData("\"9007199254740993\"", "9007199254740993")]
    [InlineData("1.000", "1")]
    [InlineData("-0", "0")]
    [InlineData("0e999999999999999999", "0")]
    public void IntegerIsExactAcrossLexicalForms(string json, string expected)
    {
        var result = Read(json, FieldType.Integer);
        Assert.Null(result.Error);
        Assert.Equal(FieldType.Integer, result.Value!.Type);
        Assert.Equal(expected, result.Value.ToString());
    }

    [Theory]
    [InlineData("9223372036854775808", QualityCode.Overflow)]
    [InlineData("-9223372036854775809", QualityCode.Overflow)]
    [InlineData("18446744073709551615", QualityCode.Overflow)]
    [InlineData("1e99999999999999999", QualityCode.Overflow)]
    [InlineData("1e-99999999999999999", QualityCode.ConversionFailed)]
    [InlineData("1.000000000000000000000000000001", QualityCode.ConversionFailed)]
    public void IntegerNeverPassesThroughRoundedDecimalOrDouble(string json, QualityCode expected)
    {
        var result = Read(json, FieldType.Integer);
        Assert.Null(result.Value);
        Assert.Equal(expected, result.Error);
    }

    [Theory]
    [InlineData("18446744073709551615", "18446744073709551615")]
    [InlineData("7.9228162514264337593543950335e28", "79228162514264337593543950335")]
    [InlineData("0.0000000000000000000000000001", "0.0000000000000000000000000001")]
    [InlineData("1e3", "1000")]
    [InlineData("100000000000000000000000000000000e-30", "100")]
    public void NumberUsesDecimalOnlyWhenExact(string json, string expected)
    {
        var result = Read(json, FieldType.Number);
        Assert.Null(result.Error);
        Assert.True(result.Value!.TryGetExactNumber(out _));
        Assert.Equal(FieldType.Number, result.Value.Type);
        Assert.Equal(expected, result.Value.ToString());
    }

    [Theory]
    [InlineData("1.000000000000000000000000000001")]
    [InlineData("1e-29")]
    [InlineData("1e100")]
    public void UnrepresentableDecimalUsesFiniteDoubleRatherThanSilentDecimalRounding(string json)
    {
        var result = Read(json, FieldType.Number);
        Assert.Null(result.Error);
        Assert.True(result.Value!.TryGetFloatingNumber(out var actual));
        Assert.Equal(double.Parse(json, CultureInfo.InvariantCulture), actual);
    }

    [Theory]
    [InlineData("true", FieldType.Boolean, "true")]
    [InlineData("false", FieldType.String, "false")]
    [InlineData("\"true\"", FieldType.Boolean, "true")]
    [InlineData("0", FieldType.Boolean, "false")]
    [InlineData("1", FieldType.Boolean, "true")]
    [InlineData("9223372036854775807", FieldType.String, "9223372036854775807")]
    [InlineData("\"001\"", FieldType.String, "001")]
    public void FixedTypesFollowExplicitConversions(string json, FieldType type, string expected)
    {
        var result = Read(json, type);
        Assert.Null(result.Error);
        Assert.Equal(type, result.Value!.Type);
        Assert.Equal(expected, result.Value.ToString());
    }

    [Theory]
    [InlineData("true", FieldType.Integer)]
    [InlineData("\"1\"", FieldType.Boolean)]
    [InlineData("\"TRUE\"", FieldType.Boolean)]
    [InlineData("2", FieldType.Boolean)]
    [InlineData("18446744073709551615", FieldType.Boolean)]
    [InlineData("null", FieldType.Number)]
    [InlineData("{}", FieldType.String)]
    [InlineData("[]", FieldType.String)]
    [InlineData("\"1,25\"", FieldType.Number)]
    [InlineData("\"1,000\"", FieldType.Integer)]
    [InlineData("\"1\\u0000\"", FieldType.Number)]
    [InlineData("\" 1\"", FieldType.Number)]
    public void InvalidScalarsNeverBecomeZeroOrJsonStrings(string json, FieldType type)
    {
        var result = Read(json, type);
        Assert.Null(result.Value);
        Assert.Equal(QualityCode.ConversionFailed, result.Error);
    }

    [Fact]
    public void NonFiniteAndCultureAreHandledExplicitly()
    {
        Assert.Equal(QualityCode.NonFinite, Read("1e999", FieldType.Number).Error);
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            Assert.Equal("1.25", Read("\"1.25\"", FieldType.Number).Value!.ToString());
            Assert.Equal(QualityCode.ConversionFailed, Read("\"1,25\"", FieldType.Number).Error);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void ThousandsOfGeneratedSignedIntegersRoundTripThroughExponentForms()
    {
        var random = new Random(20261008);
        for (var index = 0; index < 5000; index++)
        {
            var value = random.NextInt64(long.MinValue, long.MaxValue);
            var digits = value.ToString(CultureInfo.InvariantCulture);
            var json = digits + "000e-3";
            var result = Read(json, FieldType.Integer);
            Assert.Null(result.Error);
            Assert.Equal(digits, result.Value!.ToString());
        }
    }

    private static FieldConversionResult Read(string json, FieldType type)
    {
        using var document = JsonDocument.Parse(json);
        return JsonFieldValueReader.Read(document.RootElement, type);
    }
}
