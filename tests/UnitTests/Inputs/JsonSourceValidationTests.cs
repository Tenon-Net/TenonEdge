using TenonEdge.Abstractions.Messages;
using TenonEdge.Adapters.Simulator;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Devices;
using TenonEdge.Host.Features.Inputs;
using TenonEdge.Runtime.Mapping;

namespace TenonEdge.UnitTests.Inputs;

public sealed class JsonSourceValidationTests
{
    private static JsonSourceField[] Fields => [new("temperature", "/values/0", FieldType.Number, "温度", "℃")];

    [Fact]
    public void JsonFieldMetadataAndEmptyDraftsAreAllowedWithoutSamples()
    {
        var source = new MqttSourceDraft("plant/meter", Fields);
        JsonSourceValidation.Validate(source);
        JsonSourceValidation.Validate(source with { Fields = [] });
        JsonSourceValidation.Validate(source with { ProcessRetained = true, UpstreamTime = new("/time", UpstreamTimeFormat.Rfc3339) });
    }

    [Theory]
    [InlineData("topic")]
    [InlineData("qos")]
    [InlineData("retained")]
    [InlineData("pointer")]
    [InlineData("duplicate")]
    [InlineData("type")]
    [InlineData("time")]
    [InlineData("missingType")]
    [InlineData("missingFormat")]
    public void InvalidMqttAndFieldDeclarationsAreRejected(string defect)
    {
        var source = new MqttSourceDraft("plant/meter", Fields);
        source = defect switch
        {
            "topic" => source with { TopicFilter = "plant/#/bad" },
            "qos" => source with { Qos = 2 },
            "retained" => source with { ProcessRetained = true },
            "pointer" => source with { Fields = [Fields[0] with { Path = "/values/~2" }] },
            "duplicate" => source with { Fields = [Fields[0], Fields[0]] },
            "type" => source with { Fields = [Fields[0] with { Type = (FieldType)999 }] },
            "missingType" => source with { Fields = [Fields[0] with { Type = null }] },
            "missingFormat" => source with { UpstreamTime = new("/stamp", null) },
            "time" => source with { UpstreamTime = new("/time", (UpstreamTimeFormat)999) },
            _ => throw new InvalidOperationException(),
        };
        Assert.ThrowsAny<ArgumentException>(() => JsonSourceValidation.Validate(source));
    }

    [Theory]
    [InlineData("https://other.invalid/")]
    [InlineData("../outside")]
    [InlineData("values?api_key=not-real")]
    [InlineData("values?PASSWORD=not-real")]
    public void HttpSourceCannotEscapeConnectionOrStoreSecretQueryNames(string path)
    {
        var connection = Connection();
        Assert.ThrowsAny<ArgumentException>(() => JsonSourceValidation.Normalize(new(path, Fields), connection));
    }

    [Fact]
    public void HttpPreservesNonsecretQueryAndHeaderButRejectsSecretAndDuplicateHeaders()
    {
        var source = new HttpSourceDraft("values?device=meter", Fields, Headers: new() { ["X-Site"] = "plant" });
        var result = JsonSourceValidation.Normalize(source, Connection());
        Assert.Equal(source.Path, result.Path);
        Assert.Equal("plant", result.Headers!["X-Site"]);
        Assert.ThrowsAny<ArgumentException>(() => JsonSourceValidation.Normalize(source with { Headers = new() { ["X-Access-Token"] = "not-real" } }, Connection()));
        Assert.ThrowsAny<ArgumentException>(() => JsonSourceValidation.Normalize(source with { Headers = new() { ["X-Site"] = "a", ["x-site"] = "b" } }, Connection()));
    }

    [Fact]
    public void SimulatorKeepsExactCustomJsonAndDescribesOnlyConfiguredFields()
    {
        const string json = """{"count":9007199254740993,"healthy":true}""";
        var source = SimulatorSourceValidation.Normalize(new(SampleJson: json, MissingField: 0.1), "meter", "sim");
        Assert.Equal(json, source.SampleJson);
        Assert.Equal(["count", "healthy"], SimulatorSourceValidation.FieldKeys(source));
        var template = SimulatorSourceValidation.Normalize(new(), "meter", "sim");
        Assert.Equal("meter", template.Template);
        Assert.Equal(SimulatorInputFactory.MeterFields.Keys.Order(), SimulatorSourceValidation.FieldKeys(template).Order());
    }

    [Theory]
    [InlineData("""{"a":1,"a":2}""")]
    [InlineData("""{"a":null}""")]
    [InlineData("""{"a":{"nested":1}}""")]
    [InlineData("""{"bad key":1}""")]
    public void InvalidSimulatorSamplesAreRejected(string sample) =>
        Assert.ThrowsAny<ArgumentException>(() => SimulatorSourceValidation.Normalize(new(SampleJson: sample), "meter", "sim"));

    [Fact]
    public void SimulatorRejectsConflictingModesInvalidCycleAndFaults()
    {
        foreach (var source in new SimulatorSourceDraft[] {
            new(999), new(Template: "unknown"), new(Template: "meter", SampleJson: "{}"),
            new(MissingField: -0.1), new(InvalidValue: 1.1), new(ConnectionError: double.NaN) })
        { Assert.ThrowsAny<ArgumentException>(() => SimulatorSourceValidation.Normalize(source, "meter", "sim")); }
    }

    private static ConnectionDraft Connection() => ConnectionDraftValidation.Normalize(1,
        new("v", "http", "HTTP", ConnectionKind.Http, Http: new("https://example.test/api/")));
}
