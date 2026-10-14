using System.Text;
using System.Text.Json;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Abstractions.Plans;
using TenonEdge.Runtime.Diagnostics;

namespace TenonEdge.UnitTests.Runtime;

public sealed class RuntimeMessageCacheTests
{
    private static readonly RuntimeUnitId A = RuntimeUnitId.Create("input.http", "device/a");
    private static readonly RuntimeUnitId B = RuntimeUnitId.Create("input.modbus", "device/b");
    private static long Put(RuntimeMessageCache cache, RuntimeUnitId id, string text, long revision = 1) =>
        cache.Capture(id, revision, Guid.NewGuid(), Encoding.UTF8.GetBytes(text));

    [Fact]
    public void LoweringBudgetsEvictsOldestImmediatelyAndRaisingDoesNotResurrect()
    {
        var cache = new RuntimeMessageCache(TimeProvider.System);
        var payload = new string('x', 64000);
        Put(cache, A, payload);
        Put(cache, B, payload);
        var a2 = Put(cache, A, payload);
        var b2 = Put(cache, B, payload);
        cache.Configure(67584, 67584, 1);
        Assert.Empty(cache.Read(A, true));
        Assert.Equal(b2, Assert.Single(cache.Read(B, true)).Sequence);
        Assert.InRange(cache.Status.ChargedBytes, 1, 67584);
        cache.Configure(64 * 1024 * 1024, 1024 * 1024 * 1024, 100);
        Assert.Empty(cache.Read(A, true));
        Assert.Equal(b2, Assert.Single(cache.Read(B, true)).Sequence);
        Assert.False(cache.TryReadComplete(A, a2, 1, Guid.NewGuid(), out _));
        for (var i = 0; i < 30; i++)
        { Put(cache, A, payload); }
        Assert.Equal(30, cache.Read(A, true).Count); // Raised object budget, no old 1 MiB cap.
        Assert.True(cache.Status.ChargedBytes > RuntimeMessageCache.MaximumObjectBytes);
        cache.Configure(128000, 256000, 100);
        Assert.Single(cache.Read(A, true));
    }

    [Fact]
    public async Task ConcurrentCaptureAndReconfigurationShareOneEvictionBoundary()
    {
        var cache = new RuntimeMessageCache(TimeProvider.System);
        var capture = Task.Run(() =>
        {
            for (var i = 0; i < 5000; i++)
            { Put(cache, i % 2 == 0 ? A : B, "fixture"); }
        }, TestContext.Current.CancellationToken);
        for (var i = 0; i < 500; i++)
        { cache.Configure(67584, 135168, i % 10 + 1); }
        await capture;
        cache.Configure(67584, 67584, 2);
        Assert.InRange(cache.Read(A, true).Count, 0, 2);
        Assert.InRange(cache.Read(B, true).Count, 0, 2);
        Assert.InRange(cache.Status.ChargedBytes, 0, 67584);
        Assert.All(cache.Read(A, true), r => Assert.Equal("fixture", r.Body));
    }

    [Theory]
    [InlineData(DiagnosticSampleAccess.Unknown, false)]
    [InlineData(DiagnosticSampleAccess.Redacted, false)]
    [InlineData(DiagnosticSampleAccess.Safe, true)]
    public void JsonPrivacyGateRetainsOnlyCheckedSafeBodies(DiagnosticSampleAccess access, bool allowed)
    {
        var cache = new RuntimeMessageCache(TimeProvider.System);
        using var json = JsonDocument.Parse("{\"echo\":\"fixture-secret\"}");
        var now = DateTimeOffset.UtcNow;
        var message = new EdgeMessage(Guid.NewGuid(), "a", "device", now, TimeSource.Gateway, now,
            new Dictionary<string, FieldValue> { ["value"] = FieldValue.FromString("fixture-secret") },
            rawJson: RawJsonPayload.CopyFrom(json.RootElement), diagnosticAccess: access);
        var sequence = cache.CaptureInput(A, 1, message);
        var record = Assert.Single(cache.Read(A, true));
        Assert.Equal(!allowed, record.PrivacyBlocked);
        Assert.False(record.Truncated);
        Assert.Equal(allowed, cache.TryReadComplete(A, sequence, 1, message.SourceMessageId, out _));
        Assert.Equal(InputMessageSnapshot.Encode(message).OriginalBytes, record.OriginalBytes);
        if (!allowed)
        {
            Assert.Null(record.Body);
            Assert.Equal(0, record.StoredBytes);
            Assert.Equal(RuntimeMessageCache.MetadataCharge, cache.Status.ChargedBytes);
        }
        else
        { Assert.Contains("fixture-secret", record.Body!, StringComparison.Ordinal); }
        Assert.DoesNotContain("diagnosticAccess", JsonSerializer.Serialize(message), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CaptureOwnsOnlyItsPrefixAndNeverExposesItsStoredArray()
    {
        var cache = new RuntimeMessageCache(TimeProvider.System);
        var bytes = Encoding.UTF8.GetBytes("fixture-value");
        var sequence = cache.Capture(A, 1, Guid.NewGuid(), bytes);
        bytes[0] = (byte)'x';
        Assert.Equal("fixture-value", Assert.Single(cache.Read(A, true)).Body);
        Assert.True(cache.TryReadComplete(A, sequence, 1, MessageId(cache, A, sequence), out var copy));
        copy[0] = (byte)'x';
        Assert.Equal("fixture-value", Assert.Single(cache.Read(A, true)).Body);
        Assert.Null(Assert.Single(cache.Read(A, false)).Body);
        Assert.DoesNotContain("fixture-value", Assert.Single(cache.Read(A, true)).ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1, "")]
    [InlineData(2, "")]
    [InlineData(3, "")]
    [InlineData(4, "😀")]
    [InlineData(5, "😀a")]
    public void TruncatesAtUtf8RuneBoundaries(int bytes, string expected)
    {
        var cache = new RuntimeMessageCache(TimeProvider.System, bytes, 4096, 8192, 100);
        var sequence = Put(cache, A, "😀ab");
        var record = Assert.Single(cache.Read(A, true));
        Assert.Equal(expected, record.Body);
        Assert.Equal(6, record.OriginalBytes);
        Assert.True(record.Truncated);
        Assert.Equal(Encoding.UTF8.GetByteCount(expected), record.StoredBytes);
        Assert.False(cache.TryReadComplete(A, sequence, 1, MessageId(cache, A, sequence), out _));
        Assert.Equal(record.StoredBytes + RuntimeMessageCache.MetadataCharge, cache.Status.ChargedBytes);
    }

    [Fact]
    public void ObjectCountEvictsOldestEvenWhenWallClockMovesBackwards()
    {
        var time = new ReversibleClock();
        var cache = new RuntimeMessageCache(time, 16, 10000, 20000, 2);
        var oldest = Put(cache, A, "old");
        time.Now = time.Now.AddHours(-1);
        var second = Put(cache, A, "second");
        var newest = Put(cache, A, "new");
        Assert.Equal([newest, second], cache.Read(A, true).Select(row => row.Sequence));
        Assert.False(cache.TryReadComplete(A, oldest, 1, MessageId(cache, A, oldest), out _));
        Assert.Equal(2, cache.Status.Records);
    }

    [Fact]
    public void ObjectAndGlobalByteBudgetsEvictInCaptureOrderAndDropEmptyIndexes()
    {
        var charge = RuntimeMessageCache.MetadataCharge + 16;
        var cache = new RuntimeMessageCache(TimeProvider.System, 16, charge * 2, charge * 3, 100);
        var a1 = Put(cache, A, new string('a', 16));
        var b1 = Put(cache, B, new string('b', 16));
        Put(cache, A, new string('c', 16));
        Put(cache, A, new string('d', 16)); // object budget removes a1, not b1
        Assert.False(cache.TryReadComplete(A, a1, 1, MessageId(cache, A, a1), out _));
        Assert.True(cache.TryReadComplete(B, b1, 1, MessageId(cache, B, b1), out _));
        var c = RuntimeUnitId.Create("output.http", "route/out");
        Put(cache, c, new string('e', 16)); // global budget removes b1
        Assert.Empty(cache.Read(B, true));
        Assert.Equal(2, cache.Status.Objects);
        Assert.Equal(charge * 3, cache.Status.ChargedBytes);
        Assert.Equal(48, cache.Status.StoredBytes);
    }

    [Fact]
    public void RemovedObjectsAndRestartReleaseAllTheirRecords()
    {
        var cache = new RuntimeMessageCache(TimeProvider.System);
        Put(cache, A, "a");
        Put(cache, B, "b");
        cache.RetainObjects(new HashSet<RuntimeUnitId> { A });
        Assert.Empty(cache.Read(B, true));
        Assert.Single(cache.Read(A, true));
        cache.RetainObjects(new HashSet<RuntimeUnitId>());
        Assert.Equal(new RuntimeMessageCacheStatus(0, 0, 0, 0), cache.Status);
        Assert.Empty(new RuntimeMessageCache(TimeProvider.System).Read(A, true));
    }

    [Fact]
    public void SampleLookupRequiresExactObjectRevisionAndUntruncatedRecord()
    {
        var cache = new RuntimeMessageCache(TimeProvider.System);
        var sequence = Put(cache, A, "complete", 4);
        Assert.False(cache.TryReadComplete(B, sequence, 4, MessageId(cache, B, sequence), out _));
        Assert.False(cache.TryReadComplete(A, sequence, 3, MessageId(cache, A, sequence), out _));
        Assert.False(cache.TryReadComplete(A, sequence + 1, 4, MessageId(cache, A, sequence + 1), out _));
        Assert.True(cache.TryReadComplete(A, sequence, 4, MessageId(cache, A, sequence), out _));
        var truncated = Put(cache, A, new string('x', RuntimeMessageCache.MaximumRecordBytes + 1), 4);
        Assert.False(cache.TryReadComplete(A, truncated, 4, MessageId(cache, A, truncated), out _)); // no fallback to sequence
    }

    [Fact]
    public void DefaultObjectCountAndBudgetsStayBoundedUnderConcurrentCapture()
    {
        var cache = new RuntimeMessageCache(TimeProvider.System);
        Parallel.For(0, 3000, i => Put(cache, i % 2 == 0 ? A : B, new string('x', 70000)));
        Assert.InRange(cache.Status.ChargedBytes, 1, RuntimeMessageCache.MaximumGlobalBytes);
        Assert.InRange(cache.Read(A, false).Count, 1, RuntimeMessageCache.MaximumObjectRecords);
        Assert.All(cache.Read(A, false), entry =>
        {
            Assert.Equal(70000, entry.OriginalBytes);
            Assert.Equal(RuntimeMessageCache.MaximumRecordBytes, entry.StoredBytes);
            Assert.True(entry.Truncated);
        });
        var all = cache.Read(A, false).Concat(cache.Read(B, false)).ToArray();
        Assert.Equal(all.Length, all.Select(entry => entry.Sequence).Distinct().Count());
        Assert.True(all.Sum(entry => entry.StoredBytes + RuntimeMessageCache.MetadataCharge) == cache.Status.ChargedBytes);
    }

    [Fact]
    public void DefaultGlobalBudgetEvictsAcrossManyObjectsAndRetainsNoEmptyIndexes()
    {
        var cache = new RuntimeMessageCache(TimeProvider.System);
        var payload = new byte[RuntimeMessageCache.MaximumRecordBytes + 10];
        Array.Fill(payload, (byte)'a');
        for (var i = 0; i < 700; i++)
        { cache.Capture(RuntimeUnitId.Create("input.http", "device/source" + i), 1, Guid.NewGuid(), payload); }
        Assert.Empty(cache.Read(RuntimeUnitId.Create("input.http", "device/source0"), true));
        Assert.Single(cache.Read(RuntimeUnitId.Create("input.http", "device/source699"), true));
        var expected = RuntimeMessageCache.MaximumGlobalBytes / (RuntimeMessageCache.MaximumRecordBytes + RuntimeMessageCache.MetadataCharge);
        Assert.Equal(expected, cache.Status.Records);
        Assert.Equal(expected, cache.Status.Objects);
        Assert.Equal((long)expected * RuntimeMessageCache.MaximumRecordBytes, cache.Status.StoredBytes);
        Assert.True(cache.Status.ChargedBytes <= RuntimeMessageCache.MaximumGlobalBytes);
    }

    [Fact]
    public void ExactBoundaryAndEmptyContentRemainCompleteAndCountBounded()
    {
        var cache = new RuntimeMessageCache(TimeProvider.System);
        var exact = Put(cache, A, new string('x', RuntimeMessageCache.MaximumRecordBytes));
        Assert.True(cache.TryReadComplete(A, exact, 1, MessageId(cache, A, exact), out var body));
        Assert.Equal(RuntimeMessageCache.MaximumRecordBytes, body.Length);
        for (var i = 0; i < 101; i++)
        { Put(cache, B, ""); }
        Assert.Equal(100, cache.Read(B, false).Count);
        Assert.All(cache.Read(B, false), row => Assert.False(row.Truncated));
    }

    [Fact]
    public void ReusedSequenceAfterRestartCannotReturnADifferentMessage()
    {
        var old = new RuntimeMessageCache(TimeProvider.System);
        var sequence = Put(old, A, "old");
        var id = MessageId(old, A, sequence);
        var restarted = new RuntimeMessageCache(TimeProvider.System);
        Assert.Equal(sequence, Put(restarted, A, "new"));
        Assert.False(restarted.TryReadComplete(A, sequence, 1, id, out _));
    }

    private static Guid MessageId(RuntimeMessageCache cache, RuntimeUnitId id, long sequence) =>
        cache.Read(id, false).FirstOrDefault(entry => entry.Sequence == sequence)?.SourceMessageId ?? Guid.Empty;

    [Fact]
    public void ReplacingTheOnlyRecordRecreatesAnEvictedObjectBucket()
    {
        var cache = new RuntimeMessageCache(TimeProvider.System, 8, 2056, 2056, 1);
        Put(cache, A, "12345678");
        var old = Put(cache, B, "abcdefgh");
        var current = Put(cache, B, "new");
        Assert.Empty(cache.Read(A, true));
        Assert.Equal(current, Assert.Single(cache.Read(B, true)).Sequence);
        Assert.False(cache.TryReadComplete(B, old, 1, Guid.NewGuid(), out _));
        Assert.Equal(new RuntimeMessageCacheStatus(1, 1, 3, 2051), cache.Status);
    }

    private sealed class ReversibleClock : TimeProvider
    {
        internal DateTimeOffset Now = new(2026, 10, 10, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void InvalidUtf8OrIdentityDoesNotEvictValidRecords()
    {
        var cache = new RuntimeMessageCache(TimeProvider.System);
        Put(cache, A, "keep");
        Assert.Throws<ArgumentException>(() => cache.Capture(A, 1, Guid.NewGuid(), [0xff]));
        Assert.Throws<ArgumentException>(() => cache.Capture(A, 0, Guid.NewGuid(), []));
        Assert.Throws<ArgumentException>(() => cache.Capture(A, 1, Guid.Empty, []));
        Assert.Throws<ArgumentException>(() => cache.Capture(RuntimeUnitId.Create("dispatcher", "a"), 1, Guid.NewGuid(), []));
        Assert.Equal("keep", Assert.Single(cache.Read(A, true)).Body);
    }
}
