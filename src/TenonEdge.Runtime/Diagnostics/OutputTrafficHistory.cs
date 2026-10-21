using TenonEdge.Abstractions.Contracts;
using TenonEdge.Runtime.Deliveries;

namespace TenonEdge.Runtime.Diagnostics;

public sealed record OutputTrafficPoint(DateTimeOffset At, double DurationSeconds, long Generated, long GeneratedBytes,
    long Sent, long SendFailures);
public sealed record OutputTrafficSnapshot(long ConnectionId, OutputTrafficPoint[] Points, double? RecordsPerSecond,
    double? BytesPerSecond, double? WindowSeconds, bool? Accumulating,
    long? MinimumRecordBytes = null, long? MaximumRecordBytes = null);

/// <summary>只计本进程实际生成的冻结记录和协议发送结果，不从队列净增长反推生成速率。
/// 当前计划最多500个连接，每连接最多360个10秒点；不访问SQLite或保存消息，离开页面仍采样。
/// 生成计数在分发器首次冻结时记录一次，存储/配额拒绝不把真实输入速率变为零。</summary>
public sealed class OutputTrafficHistory(TimeProvider time)
{
    public const int MaximumTargets = 500;
    private readonly Lock _gate = new();
    private readonly long _started = time.GetTimestamp();
    private readonly Dictionary<long, Bucket> _targets = [];

    internal void RetainTargets(IEnumerable<long> connections)
    {
        var ids = connections.Distinct().ToArray();
        if (ids.Length > MaximumTargets || ids.Any(id => id <= 0))
        { throw new ArgumentException("输出趋势目标超限或无效。"); }
        var keep = ids.ToHashSet();
        lock (_gate)
        {
            var now = time.GetTimestamp();
            foreach (var id in _targets.Keys.Where(id => !keep.Contains(id)).ToArray())
            { _targets.Remove(id); }
            foreach (var id in ids)
            { _targets.TryAdd(id, new Bucket(now, BucketAt(now))); }
            _targets.TrimExcess();
        }
    }

    internal void Generated(IReadOnlyList<DeliveryDraft> drafts)
    {
        lock (_gate)
        {
            foreach (var draft in drafts)
            {
                if (_targets.TryGetValue(draft.ConnectionId, out var target))
                {
                    var bytes = draft.ByteSize;
                    target.Generated++;
                    target.Bytes = checked(target.Bytes + bytes);
                    target.MinimumRecordBytes = Math.Min(target.MinimumRecordBytes ?? bytes, bytes);
                    target.MaximumRecordBytes = Math.Max(target.MaximumRecordBytes ?? bytes, bytes);
                }
            }
        }
    }

    internal void Sent(long connectionId, OutputSendStatus result)
    {
        lock (_gate)
        {
            if (!_targets.TryGetValue(connectionId, out var target))
            { return; }
            if (result == OutputSendStatus.Ack)
            { target.Sent++; }
            else
            { target.Failures++; }
        }
    }

    public void Sample()
    {
        lock (_gate)
        {
            var now = time.GetTimestamp();
            var bucket = BucketAt(now);
            foreach (var target in _targets.Values)
            {
                var duration = time.GetElapsedTime(target.PreviousAt, now);
                if (bucket <= target.PreviousBucket)
                { continue; }
                Trim(target, now);
                if (duration <= RuntimeTrendHistory.Retention)
                {
                    if (target.Points.Count >= RuntimeTrendHistory.MaximumPoints)
                    { target.Points.Dequeue(); }
                    target.Points.Enqueue((now, new(time.GetUtcNow(), duration.TotalSeconds,
                        target.Generated - target.PreviousGenerated, target.Bytes - target.PreviousBytes,
                        target.Sent - target.PreviousSent, target.Failures - target.PreviousFailures),
                        target.MinimumRecordBytes, target.MaximumRecordBytes));
                }
                target.PreviousAt = now;
                target.PreviousBucket = bucket;
                target.PreviousGenerated = target.Generated;
                target.PreviousBytes = target.Bytes;
                target.PreviousSent = target.Sent;
                target.PreviousFailures = target.Failures;
                target.MinimumRecordBytes = target.MaximumRecordBytes = null;
            }
        }
    }

    public OutputTrafficSnapshot Read(long connectionId, bool includePoints = true)
    {
        lock (_gate)
        {
            if (!_targets.TryGetValue(connectionId, out var target))
            { return new(connectionId, [], null, null, null, null); }
            var now = time.GetTimestamp();
            Trim(target, now);
            var points = includePoints ? target.Points.Select(item => item.Point).ToArray() : [];
            if (target.Points.Count == 0 || time.GetElapsedTime(target.PreviousAt, now) > 2 * RuntimeTrendHistory.Interval)
            { return new(connectionId, points, null, null, null, null); }
            var recent = target.Points.TakeLast(6).ToArray();
            var seconds = recent.Sum(item => item.Point.DurationSeconds);
            return new(connectionId, points, recent.Sum(item => (double)item.Point.Generated) / seconds,
                recent.Sum(item => (double)item.Point.GeneratedBytes) / seconds, seconds,
                recent.Sum(item => (double)item.Point.Generated) > recent.Sum(item => (double)item.Point.Sent),
                recent.Min(item => item.MinimumRecordBytes), recent.Max(item => item.MaximumRecordBytes));
        }
    }

    private void Trim(Bucket target, long now)
    {
        while (target.Points.TryPeek(out var oldest) && time.GetElapsedTime(oldest.Stamp, now) >= RuntimeTrendHistory.Retention)
        { target.Points.Dequeue(); }
    }
    private long BucketAt(long timestamp) => time.GetElapsedTime(_started, timestamp).Ticks / RuntimeTrendHistory.Interval.Ticks;

    private sealed class Bucket(long timestamp, long bucket)
    {
        internal readonly Queue<(long Stamp, OutputTrafficPoint Point, long? MinimumRecordBytes, long? MaximumRecordBytes)> Points = new();
        internal long PreviousAt = timestamp;
        internal long PreviousBucket = bucket;
        internal long Generated, Bytes, Sent, Failures;
        internal long PreviousGenerated, PreviousBytes, PreviousSent, PreviousFailures;
        internal long? MinimumRecordBytes, MaximumRecordBytes;
    }
}
