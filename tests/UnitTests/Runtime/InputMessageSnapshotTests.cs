using System.Text;
using System.Text.Json;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Abstractions.Plans;
using TenonEdge.Runtime.Diagnostics;

namespace TenonEdge.UnitTests.Runtime;

public sealed class InputMessageSnapshotTests
{
    public static IEnumerable<object[]> Values()
    {
        yield return [FieldValue.FromBoolean(true)];
        yield return [FieldValue.FromBoolean(false)];
        yield return [FieldValue.FromInt64(long.MinValue)];
        yield return [FieldValue.FromInt64(long.MaxValue)];
        yield return [FieldValue.FromUInt64(ulong.MaxValue)];
        yield return [FieldValue.FromUInt64(1)];
        yield return [FieldValue.FromDecimal(1.2300m)];
        yield return [FieldValue.FromDecimal(decimal.MaxValue)];
        yield return [FieldValue.FromDecimal(0.0000000000000000000000000001m)];
        yield return [FieldValue.FromDecimal(-0.00m)];
        yield return [FieldValue.FromSingle(23.7f)];
        yield return [FieldValue.FromSingle(-0f)];
        yield return [FieldValue.FromSingle(float.Epsilon)];
        yield return [FieldValue.FromSingle(float.MaxValue)];
        yield return [FieldValue.FromDouble(23.7)];
        yield return [FieldValue.FromDouble(-0d)];
        yield return [FieldValue.FromDouble(double.Epsilon)];
        yield return [FieldValue.FromDouble(double.MaxValue)];
        yield return [FieldValue.FromString("中文😀\n\"\\")];
        yield return [FieldValue.FromString("")];
    }

    [Theory]
    [MemberData(nameof(Values))]
    public void EveryRepresentationRoundTripsWithoutInference(FieldValue value)
    {
        var before = Message(value);
        var snapshot = InputMessageSnapshot.Encode(before);
        Assert.Equal(snapshot.OriginalBytes, snapshot.Prefix.Length);
        var after = InputMessageSnapshot.Decode(snapshot.Prefix);
        Assert.Equal(value, after.Values["value"]);
        Assert.Equal(value.ToString(), after.Values["value"].ToString());
        Assert.Equal(before.SourceMessageId, after.SourceMessageId);
        Assert.Equal(before.DeviceKey, after.DeviceKey);
        Assert.Equal(before.InputId, after.InputId);
        Assert.Equal(before.CollectedAt, after.CollectedAt);
        Assert.Equal(before.ReceivedAt, after.ReceivedAt);
        Assert.Equal(before.TimeSource, after.TimeSource);
        Assert.Equal(before.Quality, after.Quality);
        Assert.Null(after.RawJson);
    }

    [Fact]
    public void RawJsonAndUpstreamTimeSurviveIndependentDocumentDisposal()
    {
        RawJsonPayload raw;
        using (var document = JsonDocument.Parse("""{"array":[18446744073709551615,1.2300],"nested":{"text":"中文😀"}}"""))
        { raw = RawJsonPayload.CopyFrom(document.RootElement); }
        var before = Message(FieldValue.FromInt64(42), raw, TimeSource.Upstream);
        var snapshot = InputMessageSnapshot.Encode(before);
        var after = InputMessageSnapshot.Decode(snapshot.Prefix);
        Assert.Equal(raw.Root.GetRawText(), after.RawJson!.Root.GetRawText());
        Assert.Equal(before.CollectedAt, after.CollectedAt);
        Assert.Equal(TimeSource.Upstream, after.TimeSource);
        Assert.Equal(ulong.MaxValue, after.RawJson.Root.GetProperty("array")[0].GetUInt64());
    }

    [Fact]
    public void LongEscapedTextIsStreamedAndOriginalSizeIsExact()
    {
        var message = Message(FieldValue.FromString(new string('\n', 200000)));
        var small = InputMessageSnapshot.Encode(Message(FieldValue.FromString("")));
        var large = InputMessageSnapshot.Encode(message);
        Assert.Equal(small.OriginalBytes + 400000, large.OriginalBytes);
        Assert.Equal(RuntimeMessageCache.MaximumRecordBytes, large.Prefix.Length);
        Assert.True(System.Text.Unicode.Utf8.IsValid(large.Prefix));
        Assert.ThrowsAny<JsonException>(() => InputMessageSnapshot.Decode(large.Prefix));
    }

    [Fact]
    public void SerializationWorkspaceDoesNotGrowWithAnEscapedFieldOrKey()
    {
        var text = new string('\n', 2 * 1024 * 1024);
        var message = Message(FieldValue.FromString(text));
        InputMessageSnapshot.Encode(Message(FieldValue.FromBoolean(true))); // warm writer
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var snapshot = InputMessageSnapshot.Encode(message);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        Assert.True(snapshot.OriginalBytes > 4 * 1024 * 1024);
        Assert.InRange(allocated, 1, 512 * 1024);
        var time = message.ReceivedAt;
        var longKey = new EdgeMessage(Guid.NewGuid(), "input", "device", time, TimeSource.Gateway, time,
            new Dictionary<string, FieldValue> { [text] = FieldValue.FromBoolean(true) });
        allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        snapshot = InputMessageSnapshot.Encode(longKey);
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        Assert.True(snapshot.OriginalBytes > 4 * 1024 * 1024);
        Assert.InRange(allocated, 1, 512 * 1024);
    }

    [Fact]
    public void SegmentedUnicodeKeepsSurrogatePairsAndEscapes()
    {
        var value = FieldValue.FromString(new string('a', 511) + "😀中\"\n" + new string('b', 2000));
        var snapshot = InputMessageSnapshot.Encode(Message(value));
        Assert.Equal(value, InputMessageSnapshot.Decode(snapshot.Prefix).Values["value"]);
    }

    [Fact]
    public void LargeRawPayloadIsTruncatedWithoutKeepingAReplayableSideCopy()
    {
        using var document = JsonDocument.Parse("{\"value\":\"" + new string('a', 200000) + "\"}");
        var cache = new RuntimeMessageCache(TimeProvider.System);
        var message = Message(FieldValue.FromBoolean(true), RawJsonPayload.CopyFrom(document.RootElement));
        var id = RuntimeUnitId.Create("input.http", "device/input");
        var sequence = cache.CaptureInput(id, 2, message);
        var record = Assert.Single(cache.Read(id, true));
        Assert.True(record.Truncated);
        Assert.True(record.OriginalBytes > 200000);
        Assert.Equal(RuntimeMessageCache.MaximumRecordBytes, record.StoredBytes);
        Assert.False(cache.TryReadComplete(id, sequence, 2, message.SourceMessageId, out _));
    }

    [Fact]
    public void TypedCacheCapturePreservesIdentityAndRejectsWrongObjectBeforeEviction()
    {
        var cache = new RuntimeMessageCache(TimeProvider.System);
        var id = RuntimeUnitId.Create("input.modbus", "device/input");
        var message = Message(FieldValue.FromUInt64(ulong.MaxValue));
        var sequence = cache.CaptureInput(id, 3, message);
        Assert.True(cache.TryReadComplete(id, sequence, 3, message.SourceMessageId, out var bytes));
        Assert.Equal(message.Values["value"], InputMessageSnapshot.Decode(bytes).Values["value"]);
        Assert.Throws<ArgumentException>(() => cache.CaptureInput(RuntimeUnitId.Create("input.http", "device/other"), 3, message));
        Assert.Throws<ArgumentException>(() => cache.CaptureInput(RuntimeUnitId.Create("output.http", "device/input"), 3, message));
        Assert.Throws<ArgumentException>(() => cache.CaptureInput(id, 0, message));
        Assert.Equal(sequence, Assert.Single(cache.Read(id, false)).Sequence);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("[1,2]")]
    [InlineData("[\"integer\",1]")]
    [InlineData("[\"int64\",18446744073709551615]")]
    [InlineData("[\"uint64\",-1]")]
    [InlineData("[\"boolean\",1]")]
    [InlineData("[\"string\",null]")]
    [InlineData("[\"decimal\",1e99]")]
    [InlineData("[\"float32\",1e99]")]
    [InlineData("[\"float64\",1e999]")]
    [InlineData("[\"int64\",1,2]")]
    public void InvalidFieldSnapshotsFailClosed(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Throws<JsonException>(() => FieldValue.ReadSnapshot(document.RootElement));
    }

    [Fact]
    public void UnknownSnapshotVersionAndOversizeBodyAreRejected()
    {
        var snapshot = InputMessageSnapshot.Encode(Message(FieldValue.FromBoolean(true)));
        var unknown = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(snapshot.Prefix).Replace("\"version\":1", "\"version\":2", StringComparison.Ordinal));
        Assert.Throws<JsonException>(() => InputMessageSnapshot.Decode(unknown));
        Assert.Throws<JsonException>(() => InputMessageSnapshot.Decode(new byte[RuntimeMessageCache.MaximumRecordBytes + 1]));
    }

    [Fact]
    public void PreclippedCaptureUsesFullOriginalLengthAndRejectsImpossibleLengths()
    {
        var cache = new RuntimeMessageCache(TimeProvider.System);
        var id = RuntimeUnitId.Create("input.http", "device/input");
        var message = Guid.NewGuid();
        var sequence = cache.Capture(id, 1, message, "prefix"u8, 1000000);
        var record = Assert.Single(cache.Read(id, false));
        Assert.Equal(1000000, record.OriginalBytes);
        Assert.Equal(6, record.StoredBytes);
        Assert.True(record.Truncated);
        Assert.False(cache.TryReadComplete(id, sequence, 1, message, out _));
        Assert.Throws<ArgumentException>(() => cache.Capture(id, 1, message, "prefix"u8, 5));
    }

    private static EdgeMessage Message(FieldValue value, RawJsonPayload? raw = null, TimeSource source = TimeSource.GatewayUnsynced)
    {
        var received = new DateTimeOffset(2026, 10, 10, 1, 2, 3, TimeSpan.Zero).AddTicks(1234);
        return new(Guid.NewGuid(), "input", "device", source == TimeSource.Upstream ? received.AddMinutes(-2) : received,
            source, received, new Dictionary<string, FieldValue> { ["value"] = value },
            new Dictionary<string, QualityCode> { ["missing"] = QualityCode.ReadTimeout, ["invalid"] = QualityCode.NonFinite }, raw, DiagnosticSampleAccess.Safe);
    }
}
