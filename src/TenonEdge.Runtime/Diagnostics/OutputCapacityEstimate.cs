using TenonEdge.Runtime.Deliveries;

namespace TenonEdge.Runtime.Diagnostics;

public sealed record CapacityConstraint(string Kind, double? Seconds, string Status);
public sealed record OutputCapacityEstimate(double? Seconds, string? LimitingConstraint, double? QuotaUpperBoundSeconds,
    string? QuotaLimitingConstraint, CapacityConstraint[] Constraints);

/// <summary>估算目标从现在断网（其他已积压目标继续积压）的容量，不是保留期或承诺。
/// 零速率不输出无限值；未知速率、磁盘空间或未校准的 k 不冒充完整估算。</summary>
public static class OutputCapacityEstimator
{
    /// <summary>Measured upper envelope plus 10% margin for SQLite queue.db + live WAL, 1—64 KiB payloads;
    /// see docs/capacity-calibration.md. Not valid for other SQLite/filesystem settings or larger payloads.</summary>
    public const double CalibratedDiskAmplification = 2.3;

    public static OutputCapacityEstimate Estimate(DeliveryCacheOptions options, DeliveryQueueUsage target,
        DeliveryQueueUsage global, double? recordsPerSecond, double? bytesPerSecond, double? accumulatingBytesPerSecond,
        StorageProtectionStatus storage, double? calibratedDiskAmplification = null)
        => Estimate(options.ToPolicy(), target, global, recordsPerSecond, bytesPerSecond, accumulatingBytesPerSecond,
            storage, calibratedDiskAmplification);

    public static OutputCapacityEstimate Estimate(TenonEdge.Abstractions.Contracts.DeliveryPolicy options, DeliveryQueueUsage target,
        DeliveryQueueUsage global, double? recordsPerSecond, double? bytesPerSecond, double? accumulatingBytesPerSecond,
        StorageProtectionStatus storage, double? calibratedDiskAmplification = null)
    {
        ValidateRate(recordsPerSecond);
        ValidateRate(bytesPerSecond);
        ValidateRate(accumulatingBytesPerSecond);
        if (calibratedDiskAmplification is { } k && (!double.IsFinite(k) || k <= 0))
        { throw new ArgumentOutOfRangeException(nameof(calibratedDiskAmplification)); }
        var constraints = new List<CapacityConstraint>
        {
            Limit("targetCount", options.MaximumPendingPerTarget - target.PendingCount, recordsPerSecond),
            Limit("targetBytes", options.MaximumPendingBytesPerTarget - target.PendingBytes, bytesPerSecond),
            Limit("globalBytes", options.MaximumQueueBytes - global.PendingBytes - global.QuarantinedBytes, accumulatingBytesPerSecond),
        };
        var quota = constraints.Where(item => item.Seconds is not null).MinBy(item => item.Seconds);
        if (storage.Space is not { } space)
        { constraints.Add(new("disk", null, "spaceUnknown")); }
        else
        {
            var remaining = space.AvailableBytes - Math.Max(options.MinimumDiskFreeBytes, space.TotalBytes * options.MinimumDiskFreeFraction);
            constraints.Add(remaining <= 0 ? new("disk", 0, "exhausted")
                : calibratedDiskAmplification is null ? new("disk", null, "uncalibrated")
                : Limit("disk", remaining, accumulatingBytesPerSecond * calibratedDiskAmplification));
        }
        var minimum = constraints.Where(item => item.Seconds is not null).MinBy(item => item.Seconds);
        var complete = minimum?.Seconds == 0 || constraints.All(item => item.Status is "estimated" or "noGrowth" or "exhausted");
        return new(complete ? minimum?.Seconds : null, complete ? minimum?.Kind : null, quota?.Seconds, quota?.Kind, constraints.ToArray());
    }

    private static CapacityConstraint Limit(string kind, double remaining, double? rate)
    {
        if (remaining <= 0)
        { return new(kind, 0, "exhausted"); }
        if (rate is null)
        { return new(kind, null, "rateUnknown"); }
        if (rate == 0)
        { return new(kind, null, "noGrowth"); }
        var seconds = remaining / rate.Value;
        return double.IsFinite(seconds) ? new(kind, seconds, "estimated") : new(kind, null, "rateUnknown");
    }
    private static void ValidateRate(double? rate)
    {
        if (rate is { } value && (!double.IsFinite(value) || value < 0))
        { throw new ArgumentOutOfRangeException(nameof(rate)); }
    }
}
