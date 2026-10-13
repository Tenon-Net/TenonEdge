using Microsoft.Extensions.Time.Testing;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Abstractions.Plans;
using TenonEdge.Runtime.Diagnostics;

namespace TenonEdge.UnitTests.Runtime;

public sealed class CurrentValueCacheTests
{
    private static readonly RuntimeUnitId Id = RuntimeUnitId.Create("input.modbus", "meter/main");
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 13, 0, 0, 0, TimeSpan.Zero));
    private EdgeMessage Message(Dictionary<string, FieldValue>? values = null, Dictionary<string, QualityCode>? quality = null,
        DiagnosticSampleAccess access = DiagnosticSampleAccess.Safe) => new(Guid.NewGuid(), "main", "meter",
            _time.GetUtcNow(), TimeSource.Gateway, _time.GetUtcNow(), values ?? [], quality, diagnosticAccess: access);
    private CurrentValueCache Cache(RuntimeUnitId? id = null)
    { var cache = new CurrentValueCache(_time); cache.Activate(1, [id ?? Id]); return cache; }
    private static CurrentFieldValue Read(CurrentValueCache cache, string key = "v", bool failed = false,
        bool running = true, int? period = 1000, RuntimeUnitId? id = null, long revision = 1) =>
        Assert.Single(cache.Read(id ?? Id, revision, [key], period, failed, running));

    [Fact]
    public void FailedAndMissingFieldsRetainOnlyLastSuccessfulValueAndItsOriginalTime()
    {
        var cache = Cache();
        var initial = Message(new() { ["v"] = FieldValue.FromUInt64(ulong.MaxValue) });
        cache.Capture(Id, 1, initial);
        _time.Advance(TimeSpan.FromMilliseconds(500));
        var failed = Message(quality: new() { ["v"] = QualityCode.ReadTimeout });
        cache.Capture(Id, 1, failed);
        var read = Read(cache);
        Assert.Equal("18446744073709551615", read.Text);
        Assert.Equal(FieldType.Integer, read.Type);
        Assert.Equal("bad", read.Quality);
        Assert.True(read.IsStale);
        Assert.Equal(initial.ReceivedAt, read.ReceivedAt);
        Assert.Empty(failed.Values); // 显示缓存从未修改待分发的失败消息。
        cache.Capture(Id, 1, Message());
        Assert.Equal("missing", Read(cache).Quality);
        Assert.True(Read(cache).IsStale);
        _time.Advance(TimeSpan.FromMilliseconds(200));
        cache.Capture(Id, 1, Message(new() { ["v"] = FieldValue.FromInt64(-9223372036854775807) }));
        Assert.Equal("-9223372036854775807", Read(cache).Text);
        Assert.False(Read(cache).IsStale);
        Assert.Equal(_time.GetUtcNow(), Read(cache).ReceivedAt);
    }

    [Fact]
    public void PollingFailureStopAndTwoMissedPeriodsExpireButMqttSilenceDoesNot()
    {
        var cache = Cache();
        cache.Capture(Id, 1, Message(new() { ["v"] = FieldValue.FromDecimal(1.2300m) }));
        Assert.Equal("1.2300", Read(cache).Text);
        Assert.True(Read(cache, failed: true).IsStale);
        Assert.True(Read(cache, running: false).IsStale);
        _time.Advance(TimeSpan.FromMilliseconds(1999));
        Assert.False(Read(cache).IsStale);
        _time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.True(Read(cache).IsStale);
        _time.Advance(TimeSpan.FromDays(4));
        Assert.False(Read(cache, failed: true, running: false, period: null).IsStale);
    }

    [Theory]
    [InlineData(DiagnosticSampleAccess.Redacted)]
    [InlineData(DiagnosticSampleAccess.Unknown)]
    public void UnsafeJsonClearsLastValuesAndCannotLeakThroughCurrentValueApi(DiagnosticSampleAccess access)
    {
        var id = RuntimeUnitId.Create("input.http", "meter/main");
        var cache = Cache(id);
        cache.Capture(id, 1, Message(new() { ["v"] = FieldValue.FromString("old-safe-value") }));
        cache.Capture(id, 1, Message(new() { ["v"] = FieldValue.FromString("credential-echo") }, access: access));
        Assert.Null(Read(cache, id: id).Text);
        Assert.Equal("privacyBlocked", Read(cache, id: id).Reason);
        Assert.Equal(0, cache.Status.Fields);
        cache.Capture(id, 1, Message(new() { ["other"] = FieldValue.FromBoolean(true) }));
        Assert.Null(Read(cache, id: id).Text);
    }

    [Fact]
    public void RevisionSwitchDropsOldValuesRejectsLateOldProducerAndSameRevisionRebuildRetains()
    {
        var cache = Cache();
        cache.Capture(Id, 1, Message(new() { ["v"] = FieldValue.FromBoolean(true) }));
        cache.Activate(1, [Id]);
        Assert.Equal("true", Read(cache).Text);
        cache.Activate(2, [Id]);
        cache.Capture(Id, 1, Message(new() { ["v"] = FieldValue.FromBoolean(false) }));
        Assert.Equal("notRead", Read(cache, revision: 2).Quality);
        Assert.Null(cache.LastReceived(Id, 2));
        cache.Capture(Id, 2, Message(new() { ["v"] = FieldValue.FromBoolean(false) }));
        Assert.Equal("false", Read(cache, revision: 2).Text);
        Assert.Null(Read(cache, revision: 1).Text);
        cache.Activate(3, []);
        cache.Capture(Id, 3, Message(new() { ["v"] = FieldValue.FromBoolean(true) }));
        Assert.Equal(0, cache.Status.Fields);
    }

    [Fact]
    public void MemoryIsBoundedWithExplicitEvictionAndOversizedValueInsteadOfTruncation()
    {
        var cache = Cache();
        var values = Enumerable.Range(0, 20000).ToDictionary(i => "p" + i, i => FieldValue.FromInt64(i));
        cache.Capture(Id, 1, Message(values));
        Assert.InRange(cache.Status.ChargedBytes, 1, CurrentValueCache.MaximumBytes);
        Assert.True(cache.Status.Evictions > 0);
        Assert.True(cache.Status.Fields < values.Count);
        Assert.Null(Read(cache, "p0").Text);
        cache.Capture(Id, 1, Message(new() { ["v"] = FieldValue.FromString(new string('x', CurrentValueCache.MaximumValueBytes)) }));
        Assert.Null(Read(cache).Text);
        Assert.Equal("valueTooLarge", Read(cache).Reason);
        cache.Activate(2, []);
        Assert.Equal(0, cache.Status.ChargedBytes);
    }

    [Fact]
    public void NeverReadDoesNotTurnIntoZeroOrBorrowAnotherSourceAndToStringOmitsBody()
    {
        var cache = Cache();
        Assert.Equal("notRead", Read(cache).Quality);
        cache.Capture(Id, 1, Message(new() { ["v"] = FieldValue.FromString("private-point-value") }));
        Assert.DoesNotContain("private-point-value", Read(cache).ToString(), StringComparison.Ordinal);
        Assert.Null(Read(cache, id: RuntimeUnitId.Create("input.modbus", "meter/other")).Text);
    }
}
