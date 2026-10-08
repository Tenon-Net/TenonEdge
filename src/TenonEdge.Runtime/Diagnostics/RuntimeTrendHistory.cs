namespace TenonEdge.Runtime.Diagnostics;

/// <summary>本进程的活动计数；与安装以来的持久累计值分开，不将确认删除当作发送成功。</summary>
public sealed record RuntimeActivityTotals(long Received, long Sent, long ReadFailures, long SendFailures,
    long RejectedPaths, long StorageFailedRecordAttempts);

internal enum RuntimeActivity { Received, Sent, ReadFailure, SendFailure, RejectedPath, StorageFailedRecordAttempt }

public sealed record RuntimeTrendPoint(DateTimeOffset At, double UptimeSeconds, double DurationSeconds,
    long Received, long Sent, long ReadFailures, long SendFailures, long RejectedPaths, long StorageFailedRecordAttempts);

/// <summary>固定指标、最多 360 点；窗口和采样间隔使用单调时间，墙上时钟仅用于显示。</summary>
public sealed class RuntimeTrendHistory(TimeProvider time)
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan Retention = TimeSpan.FromHours(1);
    public const int MaximumPoints = 360;
    private readonly Lock _lock = new();
    private readonly Queue<(long Timestamp, RuntimeTrendPoint Point)> _points = new();
    private readonly long _started = time.GetTimestamp();
    private long _previousTimestamp = time.GetTimestamp();
    private long _previousBucket;
    private RuntimeActivityTotals _previous = new(0, 0, 0, 0, 0, 0);

    public void Sample(RuntimeActivityTotals totals)
    {
        lock (_lock)
        {
            var timestamp = time.GetTimestamp();
            var duration = time.GetElapsedTime(_previousTimestamp, timestamp);
            var uptime = time.GetElapsedTime(_started, timestamp);
            var bucket = uptime.Ticks / Interval.Ticks;
            if (bucket <= _previousBucket) { return; }
            if (duration > Retention)
            {
                // 暂停超过整个窗口，无法把期间的累计数归属到最近一小时；只重建基线，不伪造曲线。
                _points.Clear();
                _previous = totals;
                _previousTimestamp = timestamp;
                _previousBucket = bucket;
                return;
            }
            var point = new RuntimeTrendPoint(time.GetUtcNow(), uptime.TotalSeconds,
                duration.TotalSeconds, Delta(totals.Received, _previous.Received), Delta(totals.Sent, _previous.Sent),
                Delta(totals.ReadFailures, _previous.ReadFailures), Delta(totals.SendFailures, _previous.SendFailures),
                Delta(totals.RejectedPaths, _previous.RejectedPaths),
                Delta(totals.StorageFailedRecordAttempts, _previous.StorageFailedRecordAttempts));
            Trim(timestamp);
            while (_points.Count >= MaximumPoints) { _points.Dequeue(); }
            _points.Enqueue((timestamp, point));
            _previous = totals;
            _previousTimestamp = timestamp;
            _previousBucket = bucket;
        }
    }

    public IReadOnlyList<RuntimeTrendPoint> Snapshot()
    {
        lock (_lock)
        {
            Trim(time.GetTimestamp());
            return _points.Select(entry => entry.Point).ToArray();
        }
    }

    private void Trim(long now)
    {
        while (_points.TryPeek(out var oldest) && time.GetElapsedTime(oldest.Timestamp, now) >= Retention)
        { _points.Dequeue(); }
    }

    private static long Delta(long current, long previous)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(current, previous);
        return current - previous;
    }
}
