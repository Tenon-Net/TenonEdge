using System.Text;
using System.Text.Json;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Abstractions.Plans;
using TenonEdge.Runtime.Diagnostics;

namespace TenonEdge.UnitTests.Runtime;

public sealed class OutputMessageSnapshotTests
{
    private static readonly RuntimeUnitId Input = RuntimeUnitId.Create("input.http", "meter/sample");
    private static readonly RuntimeUnitId Output = RuntimeUnitId.Create("output.http", "route/out");
    private static EdgeMessage Message(DiagnosticSampleAccess access = DiagnosticSampleAccess.Safe)
    {
        var now = DateTimeOffset.UtcNow;
        return new(Guid.NewGuid(), "sample", "meter", now, TimeSource.Gateway, now,
            new Dictionary<string, FieldValue> { ["value"] = FieldValue.FromInt64(long.MaxValue) }, diagnosticAccess: access);
    }
    private static OutputMessage Payload(string text) => new(Encoding.UTF8.GetBytes(text), "/ingest", null,
        "fingerprint-not-for-debug", "account-not-for-debug", new Dictionary<string, string> { ["Content-Type"] = "application/json" });

    [Fact]
    public void SnapshotKeepsMappedExactIntegerAndExactOutputTextButOmitsAccountIdentity()
    {
        var cache = new RuntimeMessageCache(TimeProvider.System);
        var source = Message();
        var id = Guid.NewGuid();
        var payload = "{\"value\":9223372036854775807}";
        var sequence = cache.CaptureOutput(Input, Output, 1, source, source, Payload(payload), new(Input.Key, id, RuntimeMessageOutcome.Generated));
        var generated = Assert.Single(cache.Read(Output, true));
        Assert.Equal(RuntimeMessageOutcome.Generated, generated.Details!.Outcome);
        Assert.Equal(id, generated.Details.DeliveryId);
        Assert.Contains("9223372036854775807", generated.Body!, StringComparison.Ordinal);
        Assert.DoesNotContain("account-not-for-debug", generated.Body!, StringComparison.Ordinal);
        Assert.DoesNotContain("fingerprint-not-for-debug", generated.Body!, StringComparison.Ordinal);
        using var body = JsonDocument.Parse(generated.Body!);
        Assert.Equal(payload, body.RootElement.GetProperty("output").GetProperty("body").GetString());
        cache.CompleteOutput(Output, sequence, RuntimeMessageOutcome.Queued);
        var queued = Assert.Single(cache.Read(Output, false));
        Assert.Equal(RuntimeMessageOutcome.Queued, queued.Details!.Outcome);
        Assert.Null(queued.Body);
        Assert.Equal(generated.OriginalBytes, queued.OriginalBytes);
        Assert.Equal(RuntimeMessageOutcome.Generated, generated.Details.Outcome); // Old read is an immutable value.
        Assert.DoesNotContain(payload, queued.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(DiagnosticSampleAccess.Unknown)]
    [InlineData(DiagnosticSampleAccess.Redacted)]
    public void UncheckedOrSensitiveHttpSourceCannotBecomeVisibleThroughOutputMapping(DiagnosticSampleAccess access)
    {
        var cache = new RuntimeMessageCache(TimeProvider.System);
        var source = Message(access);
        var payload = Payload("{\"secret\":\"fixture-secret\"}");
        var sequence = cache.CaptureOutput(Input, Output, 2, source, source, payload, new(Input.Key, Guid.NewGuid(), RuntimeMessageOutcome.Generated));
        cache.CompleteOutput(Output, sequence, RuntimeMessageOutcome.QuotaRejected);
        var record = Assert.Single(cache.Read(Output, true));
        Assert.True(record.PrivacyBlocked);
        Assert.False(record.Truncated);
        Assert.Null(record.Body);
        Assert.Equal(0, record.StoredBytes);
        Assert.True(record.OriginalBytes > 0);
        Assert.Equal(RuntimeMessageOutcome.QuotaRejected, record.Details!.Outcome);
        Assert.Equal(RuntimeMessageCache.MetadataCharge, cache.Status.ChargedBytes);
    }

    [Fact]
    public void LargeOutputHasOnlyBoundedPrefixAndPreservesFullEncodedByteCount()
    {
        var source = Message();
        var payload = Payload(new string('"', 200_000));
        var encoded = OutputMessageSnapshot.Encode(source, payload, retainBody: true);
        Assert.Equal(RuntimeMessageCache.MaximumRecordBytes, encoded.Prefix.Length);
        Assert.True(encoded.OriginalBytes > 400_000);
        Assert.Equal(encoded.OriginalBytes, OutputMessageSnapshot.Encode(source, payload, retainBody: false).OriginalBytes);
        var cache = new RuntimeMessageCache(TimeProvider.System);
        cache.CaptureOutput(Input, Output, 1, source, source, payload, new(Input.Key, Guid.NewGuid(), RuntimeMessageOutcome.Generated));
        var record = Assert.Single(cache.Read(Output, true));
        Assert.True(record.Truncated);
        Assert.Equal(encoded.OriginalBytes, record.OriginalBytes);
        Assert.Equal(RuntimeMessageCache.MaximumRecordBytes, record.StoredBytes);
    }

    [Fact]
    public void IncrementalUtf8EscapingPreservesCharactersAcross512ByteBoundaries()
    {
        var text = new string('a', 511) + "😀中\\\"\n" + new string('b', 511) + "😀";
        var encoded = OutputMessageSnapshot.Encode(Message(), Payload(text), retainBody: true);
        Assert.Equal(encoded.Prefix.Length, encoded.OriginalBytes);
        using var json = JsonDocument.Parse(encoded.Prefix);
        Assert.Equal(text, json.RootElement.GetProperty("output").GetProperty("body").GetString());
    }

    [Fact]
    public void EvictedOrRemovedRecordCannotBeResurrectedByLateQueueCompletion()
    {
        var cache = new RuntimeMessageCache(TimeProvider.System, 65536, 67584, 67584, 1);
        var source = Message();
        var first = cache.CaptureOutput(Input, Output, 1, source, source, Payload("{}"), new(Input.Key, Guid.NewGuid(), RuntimeMessageOutcome.Generated));
        var second = cache.CaptureOutput(Input, Output, 1, source, null, null, new(Input.Key, Guid.NewGuid(), RuntimeMessageOutcome.MappingRejected, 1, 2, 0));
        cache.CompleteOutput(Output, first, RuntimeMessageOutcome.Queued);
        Assert.Equal(second, Assert.Single(cache.Read(Output, true)).Sequence);
        Assert.Equal(RuntimeMessageOutcome.MappingRejected, Assert.Single(cache.Read(Output, true)).Details!.Outcome);
        cache.RetainObjects(new HashSet<RuntimeUnitId>());
        cache.CompleteOutput(Output, second, RuntimeMessageOutcome.Queued);
        Assert.Empty(cache.Read(Output, true));
        Assert.Equal(0, cache.Status.ChargedBytes);
    }

    [Fact]
    public void InvalidUtf8IsNotSilentlyReplacedInDiagnosticCopy()
    {
        Assert.Throws<ArgumentException>(() => OutputMessageSnapshot.Encode(Message(),
            new OutputMessage(new byte[] { 0xff }, "out", null, "fingerprint", "summary"), retainBody: true));
    }
}
