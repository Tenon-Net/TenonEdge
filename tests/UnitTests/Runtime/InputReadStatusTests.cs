using TenonEdge.Abstractions.Contracts;
using TenonEdge.Runtime.Diagnostics;

namespace TenonEdge.UnitTests.Runtime;

/// <summary>
/// 数据来源的读取状态（T1-02；功能需求 §7.2、§7.4）：全部成功为正常、有字段失败为部分失败，连续 3 个周期整体失败判为离线；
/// 离线与恢复各记一条运行事件，中间的失败只更新状态。
/// </summary>
public sealed class InputReadStatusTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void HealthFollowsTheLatestReads()
    {
        var events = new Recorder();
        var status = new InputReadStatus("meter-001", "simulated", events);
        Assert.Equal(InputReadHealth.NotRead, status.Snapshot().Health);

        status.ReadCompleted(At, failedFieldCount: 0);
        Assert.Equal(InputReadHealth.Normal, status.Snapshot().Health);

        status.ReadCompleted(At.AddSeconds(5), failedFieldCount: 2);
        Assert.Equal(InputReadHealth.PartialFailure, status.Snapshot().Health);

        status.ReadFailed(At.AddSeconds(10), "连接被拒绝");
        var failing = status.Snapshot();
        Assert.Equal(InputReadHealth.Failing, failing.Health);
        Assert.Equal(At.AddSeconds(5), failing.LastSuccessAt);
        Assert.Equal("连接被拒绝", failing.LastError);
        Assert.Empty(events.Recorded);
    }

    [Fact]
    public void ThreeConsecutiveFailuresMakeTheDeviceOfflineUntilAReadSucceeds()
    {
        var events = new Recorder();
        var status = new InputReadStatus("meter-001", "simulated", events);

        status.ReadFailed(At, "超时");
        status.ReadFailed(At.AddSeconds(5), "超时");
        Assert.Equal(InputReadHealth.Failing, status.Snapshot().Health);
        status.ReadFailed(At.AddSeconds(10), "超时");
        status.ReadFailed(At.AddSeconds(15), "超时");

        var offline = status.Snapshot();
        Assert.Equal(InputReadHealth.Offline, offline.Health);
        Assert.Equal(4, offline.ConsecutiveFailures);
        var wentOffline = Assert.Single(events.Recorded);
        Assert.Equal((RuntimeEventSeverity.Warning, RuntimeEventTypes.DeviceOffline, "meter-001"), (wentOffline.Severity, wentOffline.Type, wentOffline.Target));

        status.ReadCompleted(At.AddSeconds(20), 0);
        Assert.Equal(InputReadHealth.Normal, status.Snapshot().Health);
        Assert.Equal(0, status.Snapshot().ConsecutiveFailures);
        Assert.Equal(RuntimeEventTypes.DeviceOnline, events.Recorded[^1].Type);
        Assert.Equal(2, events.Recorded.Count);
    }

    [Fact]
    public void SkippedCyclesAreCounted()
    {
        var status = new InputReadStatus("meter-001", "simulated", new Recorder());

        status.CycleSkipped(At);
        status.CycleSkipped(At.AddSeconds(5));

        Assert.Equal(2, status.Snapshot().CyclesSkipped);
        Assert.Equal(At.AddSeconds(5), status.Snapshot().LastSkippedAt);
        Assert.Equal(InputReadHealth.NotRead, status.Snapshot().Health);
    }

    [Fact]
    public void DataErrorsAreASubsetOfFailuresAndRecoveryPreservesCounts()
    {
        var status = new InputReadStatus("meter-001", "simulated", new Recorder());
        status.ReadFailed(At, "超时");
        Assert.Equal(0, status.Snapshot().DataRejected);
        status.ReadFailed(At, "超限", InputReadFailureKind.DataError);
        Assert.Equal(2, status.Snapshot().ReadsFailed);
        Assert.Equal(1, status.Snapshot().DataRejected);
        status.ReadCompleted(At, 0);
        Assert.Equal(InputReadHealth.Normal, status.Snapshot().Health);
        Assert.Equal(1, status.Snapshot().DataRejected);
        var before = status.Snapshot();
        Assert.Throws<ArgumentOutOfRangeException>(() => status.ReadFailed(At, "无效分类", (InputReadFailureKind)999));
        Assert.Equal(before, status.Snapshot());
    }

    [Fact]
    public void InvalidUpstreamTimeIsIndependentFromReadFailuresAndDoesNotEmitRawEvents()
    {
        var events = new Recorder();
        var pending = new PendingCounters();
        var status = new InputReadStatus("meter", "http", events, pending);
        status.UpstreamTimeInvalid();
        status.ReadCompleted(At, 0);
        Assert.Equal(InputReadHealth.Normal, status.Snapshot().Health);
        Assert.Equal(1, status.Snapshot().InvalidUpstreamTimes);
        Assert.Equal(0, status.Snapshot().ReadsFailed);
        Assert.Equal(0, status.Snapshot().DataRejected);
        Assert.Equal(1, pending.Snapshot()["input/meter/http/invalidUpstreamTimes"]);
        Assert.Empty(events.Recorded);
    }

    private sealed class Recorder : IRuntimeEventSink
    {
        public List<(RuntimeEventSeverity Severity, string Type, string? Target, string Description)> Recorded { get; } = [];

        public void Record(RuntimeEventSeverity severity, string type, string? target, string description) =>
            Recorded.Add((severity, type, target, description));
    }
}
