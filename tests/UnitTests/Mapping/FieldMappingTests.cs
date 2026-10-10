using System.Text.Json;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Runtime.Mapping;

namespace TenonEdge.UnitTests.Mapping;

public sealed class FieldMappingTests
{
    [Fact]
    public void MappingPreservesInputDiagnosticProvenance()
    {
        var now = DateTimeOffset.UtcNow;
        var metadata = new InputSampleMetadata(true, new(true, true, 1));
        var source = new EdgeMessage(Guid.NewGuid(), "input", "device", now, TimeSource.Gateway, now,
            new Dictionary<string, FieldValue> { ["value"] = FieldValue.FromInt64(42) },
            diagnosticAccess: DiagnosticSampleAccess.Redacted, sampleMetadata: metadata);
        var mapped = new FieldMapping([new("copy", FieldType.Integer, SourceField: "value")]).Apply(source).Message!;
        Assert.Same(metadata, mapped.SampleMetadata);
        Assert.Equal(DiagnosticSampleAccess.Redacted, mapped.DiagnosticAccess);
    }

    [Fact]
    public void MappingExpansionHasABoundedResultBeforeOutputSerialization()
    {
        var at = DateTimeOffset.UnixEpoch;
        var source = new EdgeMessage(Guid.NewGuid(), "input", "device", at, TimeSource.Gateway, at,
            new Dictionary<string, FieldValue> { ["large"] = FieldValue.FromString(new string('x', 600000)) });
        var mapping = new FieldMapping(Enumerable.Range(0, 10).Select(index =>
            new FieldMappingRule($"copy{index}", FieldType.String, SourceField: "large")));
        Assert.Throws<TenonEdge.Abstractions.Contracts.InputMessageSizeException>(() => mapping.Apply(source));
        Assert.Single(source.Values);
    }

    [Fact]
    public void RawJsonCanBeMappedAfterReaderDisposalAndAcrossIndependentBranches()
    {
        var at = DateTimeOffset.UnixEpoch;
        RawJsonPayload raw;
        using (var document = JsonDocument.Parse("""{"nested":{"value":9223372036854775807}}"""))
        { raw = RawJsonPayload.CopyFrom(document.RootElement); }
        var source = new EdgeMessage(Guid.NewGuid(), "http", "device", at, TimeSource.Gateway, at,
            new Dictionary<string, FieldValue> { ["value"] = FieldValue.FromInt64(1) }, rawJson: raw);
        var fromRaw = new FieldMapping([new("exact", FieldType.Integer, SourcePath: "/nested/value")]);
        var fromField = new FieldMapping([new("selected", FieldType.Integer, SourceField: "value")]);
        var a = fromRaw.Apply(source).Message!;
        var b = fromField.Apply(source).Message!;
        Assert.Equal(long.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture), a.Values["exact"].ToString());
        Assert.Equal("1", b.Values["selected"].ToString());
        Assert.Same(raw, a.RawJson);
        Assert.Same(raw, b.RawJson);
        Assert.Equal(a.Values["exact"], fromRaw.Apply(b).Message!.Values["exact"]);
        Assert.Equal("1", source.Values["value"].ToString());
    }

    [Fact]
    public void RenameScaleSchemaAndIdentityAreIndependentAcrossBranches()
    {
        var source = Message();
        var first = new FieldMapping([new("kilowatts", FieldType.Number, SourceField: "power", Unit: "kW", Scale: 0.001m)]);
        var second = new FieldMapping([new("watts", FieldType.Integer, SourceField: "power", Unit: "W")]);
        var a = first.Apply(source).Message!;
        var b = second.Apply(source).Message!;
        Assert.Equal("1.500", a.Values["kilowatts"].ToString());
        Assert.Equal("1500", b.Values["watts"].ToString());
        Assert.Equal("1500", source.Values["power"].ToString());
        Assert.Equal(new MappedFieldSchema("kilowatts", FieldType.Number, "kW"), Assert.Single(first.Schema));
        Assert.Equal(source.SourceMessageId, a.SourceMessageId);
        Assert.Equal(source.CollectedAt, a.CollectedAt);
        Assert.Equal(source.TimeSource, a.TimeSource);
        Assert.Equal(source.ReceivedAt, a.ReceivedAt);
    }

    [Fact]
    public void RequiredFailureDiscardsWholePathWithoutAffectingAnotherBranch()
    {
        var source = Message();
        var rejected = new FieldMapping([
            new("good", FieldType.Integer, SourceField: "power"),
            new("required", FieldType.Number, SourceField: "missing", Required: true)]).Apply(source);
        Assert.Null(rejected.Message);
        Assert.True(Assert.Single(rejected.Issues).Required);
        Assert.NotNull(new FieldMapping([new("good", FieldType.Integer, SourceField: "power")]).Apply(source).Message);
    }

    [Fact]
    public void OptionalMissingAndBadAndConversionFailureAreDistinct()
    {
        var mapping = new FieldMapping([
            new("missing", FieldType.Number, SourceField: "missing"),
            new("bad", FieldType.Number, SourceField: "offline"),
            new("invalid", FieldType.Integer, Constant: FieldValue.FromString("not-number"))]);
        var result = mapping.Apply(Message());
        Assert.Empty(result.Message!.Values);
        Assert.False(result.Message.Quality.ContainsKey("missing"));
        Assert.Equal(QualityCode.ReadTimeout, result.Message.Quality["bad"]);
        Assert.Equal(QualityCode.ConversionFailed, result.Message.Quality["invalid"]);
        Assert.Equal(3, result.Issues.Length);
        Assert.Null(result.Issues[0].Reason);
    }

    [Fact]
    public void JsonPointersConstantsAndTypesAreExplicitAndNoOldValueIsReused()
    {
        var mapping = new FieldMapping([
            new("integer", FieldType.Integer, SourcePath: "/values/0"),
            new("rounded", FieldType.Integer, SourcePath: "/fraction", DecimalPlaces: 0),
            new("fixed", FieldType.String, Constant: FieldValue.FromString("fixed"))]);
        using var data = JsonDocument.Parse("{\"values\":[9223372036854775807],\"fraction\":-2.5}");
        var result = mapping.Apply(Message(), data.RootElement);
        Assert.Equal("9223372036854775807", result.Message!.Values["integer"].ToString());
        Assert.Equal("-3", result.Message.Values["rounded"].ToString());
        using var empty = JsonDocument.Parse("{}");
        var next = mapping.Apply(Message(), empty.RootElement);
        Assert.Equal("fixed", Assert.Single(next.Message!.Values).Value.ToString());
        Assert.Equal(2, next.Issues.Length);
    }

    [Fact]
    public void RequiredBadFieldKeepsOriginalReasonAndPreventsOutput()
    {
        var result = new FieldMapping([new("temperature", FieldType.Number, SourceField: "offline", Required: true)]).Apply(Message());
        Assert.Null(result.Message);
        Assert.Equal(QualityCode.ReadTimeout, Assert.Single(result.Issues).Reason);
    }

    [Fact]
    public void ScalarConversionsDoNotSilentlyChangeType()
    {
        Assert.Equal("18446744073709551615", new FieldValueConversion(FieldType.String).Convert(FieldValue.FromUInt64(ulong.MaxValue)).Value!.ToString());
        Assert.Equal("true", new FieldValueConversion(FieldType.Boolean).Convert(FieldValue.FromInt64(1)).Value!.ToString());
        Assert.Equal(QualityCode.ConversionFailed, new FieldValueConversion(FieldType.Boolean).Convert(FieldValue.FromDecimal(1m)).Error);
        Assert.Equal("2.5", new FieldValueConversion(FieldType.String, scale: 0.1m).Convert(FieldValue.FromString("25")).Value!.ToString());
        Assert.Equal("9007199254740993", new FieldValueConversion(FieldType.Integer).Convert(FieldValue.FromString("9007199254740993")).Value!.ToString());
    }

    [Fact]
    public void AmbiguousRulesAndDuplicateTargetsAreConfigurationFailures()
    {
        Assert.Throws<ArgumentException>(() => new FieldMapping([new("x", FieldType.Number)]));
        Assert.Throws<ArgumentException>(() => new FieldMapping([new("x", FieldType.Number, SourceField: "a", SourcePath: "/a")]));
        Assert.Throws<ArgumentException>(() => new FieldMapping([new("x", FieldType.Number, SourceField: "a"), new("x", FieldType.Number, SourceField: "b")]));
        Assert.Throws<ArgumentException>(() => new FieldMapping([new("x", FieldType.Number, SourcePath: "invalid")]));
        Assert.Throws<ArgumentException>(() => new FieldValueConversion(FieldType.Boolean, scale: 2m));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ArithmeticFailuresAreVisibleAndDoNotReturnPartialRequiredMessages(bool required)
    {
        var mapping = new FieldMapping([
            new("exact", FieldType.Number, Constant: FieldValue.FromDecimal(decimal.MaxValue), Scale: 2m, Required: required),
            new("floating", FieldType.Number, Constant: FieldValue.FromDouble(double.MaxValue), Scale: 2m),
            new("ok", FieldType.Integer, SourceField: "power")]);
        var result = mapping.Apply(Message());
        Assert.Equal(QualityCode.Overflow, result.Issues[0].Reason);
        Assert.Equal(QualityCode.NonFinite, result.Issues[1].Reason);
        if (required)
        { Assert.Null(result.Message); }
        else
        {
            Assert.Equal("ok", Assert.Single(result.Message!.Values).Key);
            Assert.Equal(QualityCode.Overflow, result.Message.Quality["exact"]);
            Assert.Equal(QualityCode.NonFinite, result.Message.Quality["floating"]);
        }
    }

    [Fact]
    public void FrozenEnvelopeUsesMappedSchemaTypesAndExactValues()
    {
        var source = Message();
        var mapping = new FieldMapping([
            new("long", FieldType.Integer, Constant: FieldValue.FromInt64(long.MaxValue)),
            new("unsignedText", FieldType.String, Constant: FieldValue.FromUInt64(ulong.MaxValue)),
            new("bad", FieldType.Number, SourceField: "offline")]);
        var message = mapping.Apply(source).Message!;
        var bytes = StandardEnvelope.Serialize(message, Guid.NewGuid());
        using var document = JsonDocument.Parse(bytes);
        var values = document.RootElement.GetProperty("values");
        Assert.Equal("9223372036854775807", values.GetProperty("long").GetRawText());
        Assert.Equal("18446744073709551615", values.GetProperty("unsignedText").GetString());
        Assert.False(values.TryGetProperty("bad", out _));
        Assert.Contains("readTimeout", document.RootElement.GetProperty("quality").GetRawText(), StringComparison.Ordinal);
    }

    private static EdgeMessage Message()
    {
        var at = new DateTimeOffset(2026, 10, 8, 0, 0, 0, TimeSpan.Zero);
        return new(Guid.NewGuid(), "input", "device", at, TimeSource.Gateway, at,
            new Dictionary<string, FieldValue> { ["power"] = FieldValue.FromInt64(1500) },
            new Dictionary<string, QualityCode> { ["offline"] = QualityCode.ReadTimeout });
    }
}
