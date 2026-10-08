using System.Text.Json;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Runtime.Mapping;

namespace TenonEdge.UnitTests.Mapping;

public sealed class JsonInputDecoderTests
{
    [Theory]
    [InlineData("[]")]
    [InlineData("[null]")]
    [InlineData("[{\"key\":\"n\",\"path\":\"/n\"}]")]
    [InlineData("[{\"key\":\"n\",\"path\":\"/n\",\"type\":99}]")]
    [InlineData("[{\"key\":\"n\",\"path\":\"$.n\",\"type\":\"number\"}]")]
    [InlineData("[{\"key\":\"n\",\"path\":\"/n\",\"type\":\"number\",\"secret\":\"no\"}]")]
    public void InvalidFieldDefinitionsAreRejected(string fields)
    {
        using var json = JsonDocument.Parse(fields);
        Assert.ThrowsAny<Exception>(() => new JsonInputDecoderFactory().Build(json.RootElement));
    }

    [Fact]
    public void ExactIntegersMissingAndBadAreIndependentOnEveryRead()
    {
        using var config = JsonDocument.Parse("""[{"key":"n","path":"/value","type":"integer"}]""");
        var decoder = new JsonInputDecoderFactory().Build(config.RootElement);
        using var good = JsonDocument.Parse("""{"value":9223372036854775807}""");
        var first = decoder.Decode(good.RootElement, DateTimeOffset.UnixEpoch, ClockSyncState.Synchronized, TestContext.Current.CancellationToken);
        Assert.Equal(long.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture), first.Values["n"].ToString());
        using var missing = JsonDocument.Parse("{}");
        Assert.Empty(decoder.Decode(missing.RootElement, DateTimeOffset.UnixEpoch, ClockSyncState.Synchronized, TestContext.Current.CancellationToken).Values);
        using var invalid = JsonDocument.Parse("""{"value":"broken"}""");
        var bad = decoder.Decode(invalid.RootElement, DateTimeOffset.UnixEpoch, ClockSyncState.Synchronized, TestContext.Current.CancellationToken);
        Assert.Empty(bad.Values);
        Assert.Equal(QualityCode.ConversionFailed, bad.Quality["n"]);
        Assert.Empty(first.Quality);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"path\":\"/at\"}")]
    [InlineData("{\"path\":\"/at\",\"format\":\"unix\"}")]
    [InlineData("{\"path\":\"/at\",\"format\":1}")]
    [InlineData("{\"path\":\"/at\",\"format\":\"rfc3339\",\"source\":\"gateway\"}")]
    [InlineData("{\"path\":\"/at\",\"format\":\"rfc3339\",\"source\":0}")]
    [InlineData("{\"path\":\"/at\",\"format\":\"rfc3339\",\"secret\":\"not-a-field\"}")]
    public void TimeConfigurationRequiresExplicitSupportedFormatAndUpstreamSource(string time)
    {
        using var config = JsonDocument.Parse("""[{"key":"n","path":"/value","type":"integer"}]""");
        using var mapping = JsonDocument.Parse(time);
        var exception = Record.Exception(() => new JsonInputDecoderFactory().Build(config.RootElement, mapping.RootElement));
        Assert.True(exception is ArgumentException or JsonException);
    }

    [Theory]
    [InlineData(false, ClockSyncState.Unknown, TimeSource.Gateway, false)]
    [InlineData(true, ClockSyncState.Unknown, TimeSource.Gateway, true)]
    [InlineData(true, ClockSyncState.NotSynchronized, TimeSource.GatewayUnsynced, true)]
    public void MissingTimeOnlyCountsAsInvalidWhenExplicitlyMapped(bool mapped, ClockSyncState clock, TimeSource expected, bool invalid)
    {
        using var config = JsonDocument.Parse("""[{"key":"n","path":"/value","type":"integer"}]""");
        using var mapping = JsonDocument.Parse("""{"path":"/at","format":"rfc3339"}""");
        using var body = JsonDocument.Parse("""{"value":42}""");
        var decoder = new JsonInputDecoderFactory().Build(config.RootElement, mapped ? mapping.RootElement : default);
        var result = decoder.Decode(body.RootElement, DateTimeOffset.UnixEpoch, clock, TestContext.Current.CancellationToken);
        Assert.Equal(DateTimeOffset.UnixEpoch, result.CollectedAt);
        Assert.Equal(expected, result.TimeSource);
        Assert.Equal(invalid, result.InvalidUpstreamTime);
        Assert.Equal("42", result.Values["n"].ToString());
    }

    [Fact]
    public void RepeatedLargeFieldSelectionHasABoundedResult()
    {
        var definitions = Enumerable.Range(0, 10).Select(index => new { key = $"f{index}", path = "/value", type = "string" });
        var decoder = new JsonInputDecoderFactory().Build(JsonSerializer.SerializeToElement(definitions));
        using var payload = JsonDocument.Parse(JsonSerializer.Serialize(new { value = new string('x', 600000) }));
        Assert.Throws<InputMessageSizeException>(() => decoder.Decode(payload.RootElement, DateTimeOffset.UnixEpoch, ClockSyncState.Synchronized, TestContext.Current.CancellationToken));
    }
}
