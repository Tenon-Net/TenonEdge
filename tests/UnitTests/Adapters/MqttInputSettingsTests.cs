using System.Text.Json;
using System.Text.Json.Nodes;
using TenonEdge.Adapters.Mqtt;
using TenonEdge.Runtime.Mapping;

namespace TenonEdge.UnitTests.Adapters;

public sealed class MqttInputSettingsTests
{
    [Theory]
    [InlineData("sensors/+/values")]
    [InlineData("sensors/#")]
    [InlineData("#")]
    [InlineData("+")]
    [InlineData("/+/状态")]
    public void ValidFiltersPreserveProtocolSyntaxAndDefaultToQosOne(string filter)
    {
        var settings = MqttInputSettings.Parse(Configuration(new JsonObject { ["topicFilter"] = filter }));
        Assert.Equal(filter, settings.TopicFilter);
        Assert.Equal(1, settings.Qos);
        Assert.False(settings.ProcessRetained);
        Assert.Equal("mqtt://localhost:1883", settings.Recipient);
        _ = new JsonInputDecoderFactory().Build(settings.Fields, settings.UpstreamTime);
    }

    [Theory]
    [InlineData("{\"topicFilter\":\"\"}")]
    [InlineData("{\"topicFilter\":null}")]
    [InlineData("{\"topicFilter\":\"a/#/b\"}")]
    [InlineData("{\"topicFilter\":\"a+\"}")]
    [InlineData("{\"topicFilter\":\"a/#suffix\"}")]
    [InlineData("{\"qos\":2}")]
    [InlineData("{\"qos\":-1}")]
    [InlineData("{\"connectionId\":0}")]
    [InlineData("{\"fields\":[]}")]
    [InlineData("{\"fields\":null}")]
    [InlineData("{\"processRetained\":true}")]
    [InlineData("{\"processRetained\":true,\"upstreamTime\":null}")]
    [InlineData("{\"processRetained\":true,\"upstreamTime\":{}}")]
    [InlineData("{\"processRetained\":true,\"upstreamTime\":{\"path\":\"/at\"}}")]
    [InlineData("{\"upstreamTime\":[]}")]
    public void InvalidSubscriptionStructureIsRejected(string patch)
    {
        Assert.ThrowsAny<ArgumentException>(() => MqttInputSettings.Parse(Configuration(JsonNode.Parse(patch)!.AsObject())));
    }

    [Theory]
    [InlineData("password")]
    [InlineData("script")]
    [InlineData("unknown")]
    public void UnknownAndSecretPropertiesAreNotSilentlyAccepted(string property)
    {
        Assert.Throws<JsonException>(() => MqttInputSettings.Parse(Configuration(new JsonObject { [property] = "not-allowed" })));
    }

    [Fact]
    public void RetainedProcessingRequiresCompilableTimeMappingAndSettingsOutliveSourceDocument()
    {
        MqttInputSettings settings;
        using (var document = JsonDocument.Parse(Configuration(JsonNode.Parse(
            """{"processRetained":true,"upstreamTime":{"path":"/at","format":"rfc3339"}}""")!.AsObject()).GetRawText()))
        {
            settings = MqttInputSettings.Parse(document.RootElement);
        }
        Assert.True(settings.ProcessRetained);
        _ = new JsonInputDecoderFactory().Build(settings.Fields, settings.UpstreamTime);
        Assert.Equal("/at", settings.UpstreamTime.GetProperty("path").GetString());
        var invalid = MqttInputSettings.Parse(Configuration(JsonNode.Parse(
            """{"processRetained":true,"upstreamTime":{"path":"/at","format":"not-a-format"}}""")!.AsObject()));
        Assert.Throws<JsonException>(() => new JsonInputDecoderFactory().Build(invalid.Fields, invalid.UpstreamTime));
    }

    [Fact]
    public void FilterLimitIsUtf8BytesAndUnpairedSurrogatesAreRejected()
    {
        MqttInputReceiver.ValidateFilter(new string('a', 65535));
        Assert.ThrowsAny<ArgumentException>(() => MqttInputReceiver.ValidateFilter(new string('a', 65536)));
        Assert.ThrowsAny<ArgumentException>(() => MqttInputReceiver.ValidateFilter(new string('中', 21846)));
        Assert.ThrowsAny<ArgumentException>(() => MqttInputReceiver.ValidateFilter("a\0b"));
        Assert.ThrowsAny<ArgumentException>(() => MqttInputReceiver.ValidateFilter("\ud800"));
    }

    private static JsonElement Configuration(JsonObject patch)
    {
        var json = JsonNode.Parse("""{"connectionId":1,"host":"localhost","topicFilter":"input/value","fields":[{"key":"value","path":"/v","type":"integer"}]}""")!.AsObject();
        foreach (var (key, value) in patch)
        { json[key] = value?.DeepClone(); }
        return JsonSerializer.SerializeToElement(json);
    }
}
