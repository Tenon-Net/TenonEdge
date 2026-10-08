using Microsoft.Extensions.Time.Testing;
using TenonEdge.Runtime.Diagnostics;

namespace TenonEdge.UnitTests.Runtime;

public sealed class RuntimeTrendHistoryTests
{
    [Fact]
    public void SchedulerJitterDoesNotSkipTheFollowingTenSecondBucket()
    {
        var time = new FakeTimeProvider();
        var history = new RuntimeTrendHistory(time);
        time.Advance(TimeSpan.FromMilliseconds(10100));
        history.Sample(Totals(1));
        time.Advance(TimeSpan.FromMilliseconds(9900));
        history.Sample(Totals(2));
        Assert.Equal(2, history.Snapshot().Count);
        Assert.Equal(9.9, history.Snapshot()[1].DurationSeconds);
        history.Sample(Totals(3));
        Assert.Equal(2, history.Snapshot().Count);
    }

    [Fact]
    public void SamplesDeltasOnlyEveryTenSecondsAndKeepsAtMostOneHour()
    {
        var time = new FakeTimeProvider();
        var history = new RuntimeTrendHistory(time);
        history.Sample(Totals(1));
        Assert.Empty(history.Snapshot());
        for (var i = 1; i <= 400; i++)
        {
            time.Advance(TimeSpan.FromSeconds(10));
            history.Sample(Totals(i));
        }

        var points = history.Snapshot();
        Assert.Equal(360, points.Count);
        Assert.Equal(410, points[0].UptimeSeconds);
        Assert.Equal(4000, points[^1].UptimeSeconds);
        Assert.All(points, point =>
        {
            Assert.Equal(10, point.DurationSeconds);
            Assert.Equal(1, point.Received);
            Assert.Equal(2, point.Sent);
            Assert.Equal(3, point.SendFailures);
        });
        time.Advance(TimeSpan.FromHours(2));
        Assert.Empty(history.Snapshot());
        history.Sample(Totals(500));
        Assert.Empty(history.Snapshot());
        time.Advance(TimeSpan.FromSeconds(10));
        history.Sample(Totals(501));
        Assert.Equal(1, Assert.Single(history.Snapshot()).Received);
        Assert.Empty(new RuntimeTrendHistory(time).Snapshot());
    }

    [Fact]
    public void WallClockJumpsDoNotExpireHistoryOrChangeSamplingDurations()
    {
        var inner = new FakeTimeProvider();
        var time = new ShiftClock(inner);
        var history = new RuntimeTrendHistory(time);
        inner.Advance(TimeSpan.FromSeconds(10));
        history.Sample(Totals(1));
        time.Shift = TimeSpan.FromDays(400);
        Assert.Single(history.Snapshot());
        inner.Advance(TimeSpan.FromSeconds(30));
        history.Sample(Totals(4));
        var last = history.Snapshot()[1];
        Assert.Equal(30, last.DurationSeconds);
        Assert.Equal(3, last.Received);
        time.Shift = TimeSpan.FromDays(-400);
        Assert.Equal(2, history.Snapshot().Count);
    }

    private static RuntimeActivityTotals Totals(long value) => new(value, value * 2, value, value * 3, value, value);
    private sealed class ShiftClock(TimeProvider inner) : TimeProvider
    {
        public TimeSpan Shift { get; set; }
        public override long TimestampFrequency => inner.TimestampFrequency;
        public override long GetTimestamp() => inner.GetTimestamp();
        public override DateTimeOffset GetUtcNow() => inner.GetUtcNow() + Shift;
    }
}
