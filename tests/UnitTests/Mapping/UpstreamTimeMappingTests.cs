using System.Text.Json;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Runtime.Mapping;

namespace TenonEdge.UnitTests.Mapping;

public sealed class UpstreamTimeMappingTests
{
    private static readonly DateTimeOffset Received = new(2026, 10, 8, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("2020-03-01T12:34:56+23:00", "2020-02-29T13:34:56.0000000+00:00")]
    [InlineData("2020-02-29T12:34:56Z", "2020-02-29T12:34:56.0000000+00:00")]
    [InlineData("2020-02-29t20:34:56z", "2020-02-29T20:34:56.0000000+00:00")]
    [InlineData("2020-02-29T20:34:56+08:00", "2020-02-29T12:34:56.0000000+00:00")]
    [InlineData("2020-02-29T12:34:56.123456789Z", "2020-02-29T12:34:56.1234567+00:00")]
    public void ParsesExplicitRfcTimeAndKeepsOldBacklogTime(string text, string expected)
    {
        var result = Map(JsonSerializer.Serialize(text), UpstreamTimeFormat.Rfc3339);
        Assert.False(result.InvalidUpstreamTime);
        Assert.Equal(TimeSource.Upstream, result.Source);
        Assert.Equal(expected, result.CollectedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("0", UpstreamTimeFormat.UnixSeconds, 0L)]
    [InlineData("1.25", UpstreamTimeFormat.UnixSeconds, 12500000L)]
    [InlineData("-1.25", UpstreamTimeFormat.UnixSeconds, -12500000L)]
    [InlineData("1250", UpstreamTimeFormat.UnixMilliseconds, 12500000L)]
    [InlineData("\"1250\"", UpstreamTimeFormat.UnixMilliseconds, 12500000L)]
    [InlineData("1e3", UpstreamTimeFormat.UnixMilliseconds, 10000000L)]
    public void UnixUnitsAreExplicitAndFractionalValuesDoNotGoThroughDouble(string raw, UpstreamTimeFormat format, long ticks)
    {
        var result = Map(raw, format);
        Assert.False(result.InvalidUpstreamTime);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddTicks(ticks), result.CollectedAt);
    }

    [Theory]
    [InlineData("\"2020-01-01\"", UpstreamTimeFormat.Rfc3339)]
    [InlineData("\"2020-01-01T01:02:03\"", UpstreamTimeFormat.Rfc3339)]
    [InlineData("\"2020-02-30T01:02:03Z\"", UpstreamTimeFormat.Rfc3339)]
    [InlineData("\"2020-01-01 01:02:03Z\"", UpstreamTimeFormat.Rfc3339)]
    [InlineData("\"2020-01-01T01:02:03Z\\n\"", UpstreamTimeFormat.Rfc3339)]
    [InlineData("\"2020-01-01T01:02:03+24:00\"", UpstreamTimeFormat.Rfc3339)]
    [InlineData("\"2020-01-01T01:02:03+00:60\"", UpstreamTimeFormat.Rfc3339)]
    [InlineData("\"2016-12-31T23:59:60Z\"", UpstreamTimeFormat.Rfc3339)]
    [InlineData("\"0001-01-01T00:00:00+01:00\"", UpstreamTimeFormat.Rfc3339)]
    [InlineData("\"1\\u0000\"", UpstreamTimeFormat.UnixSeconds)]
    [InlineData("\" 1\"", UpstreamTimeFormat.UnixSeconds)]
    [InlineData("null", UpstreamTimeFormat.UnixSeconds)]
    [InlineData("true", UpstreamTimeFormat.UnixSeconds)]
    [InlineData("{}", UpstreamTimeFormat.UnixSeconds)]
    [InlineData("\"NaN\"", UpstreamTimeFormat.UnixSeconds)]
    [InlineData("1e100", UpstreamTimeFormat.UnixSeconds)]
    [InlineData("79228162514264337593543950335", UpstreamTimeFormat.UnixMilliseconds)]
    [InlineData("253402300800", UpstreamTimeFormat.UnixSeconds)]
    public void InvalidTimeFallsBackWithoutDroppingData(string raw, UpstreamTimeFormat format)
    {
        var result = Map(raw, format);
        Assert.True(result.InvalidUpstreamTime);
        Assert.Equal(Received, result.CollectedAt);
        Assert.Equal(TimeSource.Gateway, result.Source);
    }

    [Theory]
    [InlineData(ClockSyncState.Synchronized, 300, false)]
    [InlineData(ClockSyncState.Synchronized, 301, true)]
    [InlineData(ClockSyncState.Unknown, 301, false)]
    [InlineData(ClockSyncState.NotSynchronized, 301, false)]
    public void FutureGuardOnlyAppliesWhenClockIsKnownSynchronized(ClockSyncState clock, int seconds, bool invalid)
    {
        var candidate = Received.AddSeconds(seconds);
        var result = Map(JsonSerializer.Serialize(candidate), UpstreamTimeFormat.Rfc3339, clock);
        Assert.Equal(invalid, result.InvalidUpstreamTime);
        Assert.Equal(invalid ? Received : candidate, result.CollectedAt);
    }

    [Theory]
    [InlineData(ClockSyncState.Unknown, TimeSource.Gateway)]
    [InlineData(ClockSyncState.NotSynchronized, TimeSource.GatewayUnsynced)]
    [InlineData(ClockSyncState.Synchronized, TimeSource.Gateway)]
    public void MissingTimeFallsBackPerClockStateWithoutReusingPreviousValue(ClockSyncState clock, TimeSource expected)
    {
        var mapping = new UpstreamTimeMapping("/time", UpstreamTimeFormat.UnixSeconds, TimeSource.Device);
        using var first = JsonDocument.Parse("{\"time\":0}");
        Assert.Equal(TimeSource.Device, mapping.Resolve(first.RootElement, Received, clock).Source);
        using var missing = JsonDocument.Parse("{}");
        var result = mapping.Resolve(missing.RootElement, Received, clock);
        Assert.True(result.InvalidUpstreamTime);
        Assert.Equal(Received, result.CollectedAt);
        Assert.Equal(expected, result.Source);
    }

    [Fact]
    public void ConfigurationRejectsUnspecifiedFormatsAndGatewayAsUpstreamSource()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new UpstreamTimeMapping("/time", (UpstreamTimeFormat)99, TimeSource.Device));
        Assert.Throws<ArgumentOutOfRangeException>(() => new UpstreamTimeMapping("/time", UpstreamTimeFormat.Rfc3339, TimeSource.Gateway));
    }

    private static MappedTime Map(string raw, UpstreamTimeFormat format, ClockSyncState clock = ClockSyncState.Synchronized)
    {
        using var document = JsonDocument.Parse("{\"time\":" + raw + "}");
        return new UpstreamTimeMapping("/time", format, TimeSource.Upstream).Resolve(document.RootElement, Received, clock);
    }
}
