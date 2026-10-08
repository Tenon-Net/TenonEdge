using System.Text;
using System.Text.Json;
using TenonEdge.Abstractions.Messages;

namespace TenonEdge.UnitTests.Abstractions;

public sealed class OutputPayloadFormatTests
{
    [Fact]
    public void DefaultIsByteIdenticalToStandardEnvelope()
    {
        var source = Message();
        var id = Guid.NewGuid();
        Assert.Equal(StandardEnvelope.Serialize(source, id), OutputPayloadFormat.Parse(default).Serialize(source, id));
    }

    [Fact]
    public void ValuesOnlyPreservesExactNumbersAndEscapesStringsWithoutAddingMetadata()
    {
        var bytes = Parse("""{"mode":"values"}""").Serialize(Message(), Guid.NewGuid());
        var text = Encoding.UTF8.GetString(bytes);
        Assert.Contains("18446744073709551615", text, StringComparison.Ordinal);
        Assert.Contains("0.1234567890123456789012345678", text, StringComparison.Ordinal);
        using var json = JsonDocument.Parse(bytes);
        Assert.Equal(ulong.MaxValue, json.RootElement.GetProperty("integer").GetUInt64());
        Assert.Equal("\"},\"injected\":true", json.RootElement.GetProperty("text").GetString());
        Assert.False(json.RootElement.TryGetProperty("injected", out _));
        Assert.False(json.RootElement.TryGetProperty("bad", out _));
        Assert.False(json.RootElement.TryGetProperty("quality", out _));
        Assert.False(json.RootElement.TryGetProperty("collectedAt", out _));
    }

    [Fact]
    public void AssemblySelectsFieldsLiteralConstantsAndOnlyWhitelistedPlaceholders()
    {
        var format = Parse("""
            {"mode":"custom","fields":[
              {"target":"reading","sourceField":"integer"},
              {"target":"id","placeholder":"{messageId}"},
              {"target":"source","placeholder":"{sourceMessageId}"},
              {"target":"time","placeholder":"{collectedAt}"},
              {"target":"literal","constant":"{messageId}"},
              {"target":"nothing","constant":null},
              {"target":"exact","constant":18446744073709551615},
              {"target":"omit","sourceField":"bad"}]}
            """);
        var source = Message();
        var id = Guid.NewGuid();
        using var json = JsonDocument.Parse(format.Serialize(source, id));
        Assert.Equal(id, json.RootElement.GetProperty("id").GetGuid());
        Assert.Equal(source.SourceMessageId, json.RootElement.GetProperty("source").GetGuid());
        Assert.Equal(StandardEnvelope.FormatTime(source.CollectedAt), json.RootElement.GetProperty("time").GetString());
        Assert.Equal("{messageId}", json.RootElement.GetProperty("literal").GetString());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("nothing").ValueKind);
        Assert.Equal(ulong.MaxValue, json.RootElement.GetProperty("exact").GetUInt64());
        Assert.Equal(ulong.MaxValue, json.RootElement.GetProperty("reading").GetUInt64());
        Assert.False(json.RootElement.TryGetProperty("omit", out _));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("bad")]
    public void RequiredMissingOrBadFieldRejectsWholePayload(string field)
    {
        var format = Parse($$"""{"mode":"custom","fields":[{"target":"required","sourceField":"{{field}}","required":true}]}""");
        Assert.Throws<ArgumentException>(() => format.Serialize(Message(), Guid.NewGuid()));
    }

    [Theory]
    [InlineData("""{"mode":"unknown"}""")]
    [InlineData("""{"mode":"values","fields":[]}""")]
    [InlineData("""{"mode":"custom","fields":[]}""")]
    [InlineData("""{"mode":"custom","fields":[{"target":"x","placeholder":"{deviceKey}"}]}""")]
    [InlineData("""{"mode":"custom","fields":[{"target":"x","constant":1e400}]}""")]
    [InlineData("""{"mode":"custom","fields":[{"target":"x","constant":[]}]}""")]
    [InlineData("""{"mode":"custom","fields":[{"target":"x","sourceField":"a","constant":1}]}""")]
    [InlineData("""{"mode":"custom","fields":[{"target":"x","constant":1},{"target":"x","constant":2}]}""")]
    public void InvalidFormatsFailDuringCompilation(string configuration) => Assert.Throws<ArgumentException>(() => Parse(configuration));

    [Fact]
    public void UnknownExpressionPropertiesAreNotIgnored() => Assert.Throws<JsonException>(() =>
        Parse("""{"mode":"custom","fields":[{"target":"x","constant":1,"expression":"run()"}]}"""));

    [Fact]
    public void ExpansionAndUtf8EscapingCannotExceedOutputLimit()
    {
        var fields = Enumerable.Range(0, 1000).Select(index => new { target = "x" + index, sourceField = "text" });
        var format = OutputPayloadFormat.Parse(JsonSerializer.SerializeToElement(new { mode = "custom", fields }));
        Assert.Throws<ArgumentException>(() => format.Serialize(Message(new string('x', 1000)), Guid.NewGuid()));
        Assert.Equal(StandardEnvelope.SerializationReservationBytes(StandardEnvelope.MaximumPayloadBytes), format.MinimumSerializationReservationBytes);
        var source = Message(new string('\u0001', 50000));
        Assert.Throws<ArgumentException>(() => Parse("""{"mode":"values"}""").Serialize(source, Guid.NewGuid()));
    }

    [Fact]
    public void ConstantAggregateIsBoundedBeforeCloningAllRules()
    {
        var fields = Enumerable.Range(0, 1000).Select(index => new { target = "x" + index, constant = new string('x', 1000) });
        Assert.Throws<ArgumentException>(() => OutputPayloadFormat.Parse(JsonSerializer.SerializeToElement(new { mode = "custom", fields })));
    }

    private static OutputPayloadFormat Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return OutputPayloadFormat.Parse(document.RootElement);
    }

    private static EdgeMessage Message(string? text = null)
    {
        var at = DateTimeOffset.Parse("2026-10-08T01:02:03.456Z", System.Globalization.CultureInfo.InvariantCulture).ToUniversalTime();
        return new(Guid.NewGuid(), "input", "device", at, TimeSource.Gateway, at,
            new Dictionary<string, FieldValue>
            {
                ["integer"] = FieldValue.FromUInt64(ulong.MaxValue),
                ["number"] = FieldValue.FromDecimal(0.1234567890123456789012345678m),
                ["text"] = FieldValue.FromString(text ?? "\"},\"injected\":true"),
            }, new Dictionary<string, QualityCode> { ["bad"] = QualityCode.ReadTimeout });
    }
}
