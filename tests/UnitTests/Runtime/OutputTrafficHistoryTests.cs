using Microsoft.Extensions.Time.Testing;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Runtime.Deliveries;
using TenonEdge.Runtime.Diagnostics;

namespace TenonEdge.UnitTests.Runtime;

public sealed class OutputTrafficHistoryTests
{
    private static DeliveryDraft Draft(long target) => new(Guid.NewGuid(), target, Guid.NewGuid(), "meter", 1,
        "{}"u8.ToArray(), "fp", "summary", "topic");

    [Fact]
    public void CalibrationUsesObservedExtremaInTheSameSixBucketRateWindowNotAnAverageOrPayloadCopy()
    {
        var time = new FakeTimeProvider();
        var history = new OutputTrafficHistory(time);
        history.RetainTargets([1]);
        var small = Draft(1);
        var large = new DeliveryDraft(Guid.NewGuid(), 1, Guid.NewGuid(), "meter", 1,
            new byte[DeliveryDraft.MaximumPayloadBytes], "fp", "summary", "topic",
            headers: new Dictionary<string, string> { ["X-Size"] = new string('h', 1024) });
        history.Generated([small, large]);
        time.Advance(TimeSpan.FromSeconds(10));
        history.Sample();
        Assert.Equal(small.ByteSize, history.Read(1).MinimumRecordBytes);
        Assert.Equal(large.ByteSize, history.Read(1, includePoints: false).MaximumRecordBytes);
        for (var index = 0; index < 5; index++)
        {
            history.Generated([small]);
            time.Advance(TimeSpan.FromSeconds(10));
            history.Sample();
        }
        Assert.Equal(large.ByteSize, history.Read(1).MaximumRecordBytes);
        history.Generated([small]);
        time.Advance(TimeSpan.FromSeconds(10));
        history.Sample();
        Assert.Equal(small.ByteSize, history.Read(1).MaximumRecordBytes);
        time.Advance(TimeSpan.FromSeconds(21));
        Assert.Null(history.Read(1).MinimumRecordBytes);
        Assert.Null(history.Read(1).MaximumRecordBytes);
        history.RetainTargets([]);
        history.RetainTargets([1]);
        Assert.Null(history.Read(1).MaximumRecordBytes);
    }

    [Fact]
    public void TargetActivationOffsetAndTimerJitterDoNotSkipTheMonitorSamplingBucket()
    {
        var time = new FakeTimeProvider();
        var history = new OutputTrafficHistory(time);
        time.Advance(TimeSpan.FromMilliseconds(100));
        history.RetainTargets([1]);
        history.Generated([Draft(1)]);
        time.Advance(TimeSpan.FromMilliseconds(9910)); // 全局第10秒回调，目标只激活9.91秒。
        history.Sample();
        Assert.NotNull(history.Read(1).RecordsPerSecond);
        Assert.Equal(9.91, Assert.Single(history.Read(1).Points).DurationSeconds);
        time.Advance(TimeSpan.FromMilliseconds(9990)); // 下次全局第20秒，不能因间隔略短于10秒跳过。
        history.Sample();
        Assert.Equal(2, history.Read(1).Points.Length);
        history.Sample();
        Assert.Equal(2, history.Read(1).Points.Length); // 同一时间桶不重复采样。
    }

    [Fact]
    public void ActualGeneratedBytesAndSendOutcomesAreSampledSeparatelyWithRealDurations()
    {
        var time = new FakeTimeProvider();
        var history = new OutputTrafficHistory(time);
        history.RetainTargets([1, 2]);
        Assert.Null(history.Read(1).RecordsPerSecond);
        var one = Draft(1);
        var two = Draft(2);
        history.Generated([one, one, two]);
        history.Sent(1, OutputSendStatus.Ack);
        history.Sent(1, OutputSendStatus.Retry);
        time.Advance(TimeSpan.FromSeconds(12));
        history.Sample();
        var point = Assert.Single(history.Read(1).Points);
        Assert.Equal(2, point.Generated);
        Assert.Equal(one.ByteSize * 2, point.GeneratedBytes);
        Assert.Equal(1, point.Sent);
        Assert.Equal(1, point.SendFailures);
        Assert.Equal(12, point.DurationSeconds);
        Assert.Equal(2.0 / 12, history.Read(1).RecordsPerSecond);
        Assert.True(history.Read(1, includePoints: false).Accumulating);
        Assert.Empty(history.Read(1, includePoints: false).Points);
        Assert.Equal(one.ByteSize * 2.0 / 12, history.Read(1).BytesPerSecond);
        Assert.Equal(1, Assert.Single(history.Read(2).Points).Generated);
        time.Advance(TimeSpan.FromSeconds(21));
        Assert.Null(history.Read(1).RecordsPerSecond); // 采样线程停滞时不拿旧速率当当前值。
    }

    [Fact]
    public void RetentionAndTargetMembershipBoundMemoryAndRestartDoesNotInventHistory()
    {
        var time = new FakeTimeProvider();
        var history = new OutputTrafficHistory(time);
        history.RetainTargets([1]);
        for (var i = 0; i < 400; i++)
        { history.Generated([Draft(1)]); time.Advance(TimeSpan.FromSeconds(10)); history.Sample(); }
        Assert.Equal(360, history.Read(1).Points.Length);
        history.RetainTargets([2]);
        history.Generated([Draft(1)]);
        Assert.Empty(history.Read(1).Points);
        Assert.Null(history.Read(2).RecordsPerSecond);
        history.Generated([Draft(2)]);
        time.Advance(TimeSpan.FromHours(2));
        history.Sample();
        Assert.Empty(history.Read(2).Points); // 不把2小时内的未知分布画成最近10秒。
        Assert.Empty(new OutputTrafficHistory(time).Read(2).Points);
        Assert.Throws<ArgumentException>(() => history.RetainTargets(Enumerable.Range(1, 501).Select(id => (long)id)));
    }
}
