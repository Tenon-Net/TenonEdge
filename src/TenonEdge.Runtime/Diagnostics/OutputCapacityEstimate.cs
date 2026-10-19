using TenonEdge.Runtime.Deliveries;

namespace TenonEdge.Runtime.Diagnostics;

public sealed record CapacityConstraint(string Kind, double? Seconds, string Status);
public sealed record DiskCapacityCalibration(string Status, string? Profile, double? Amplification);
public sealed record OutputCapacityEstimate(double? Seconds, string? LimitingConstraint, double? QuotaUpperBoundSeconds,
    string? QuotaLimitingConstraint, CapacityConstraint[] Constraints, DiskCapacityCalibration? Calibration = null);

/// <summary>估算目标从现在断网（其他已积压目标继续积压）的容量，不是保留期或承诺。
/// 零速率不输出无限值；未知速率、磁盘空间或未校准的 k 不冒充完整估算。</summary>
public static class OutputCapacityEstimator
{
    /// <summary>Empirical planning model, not a safety bound. Per-record WAL/index work must not be
    /// diluted by using a large-payload-only coefficient; see docs/capacity-calibration.md.</summary>
    public const double CalibratedBaseAmplification = 2.3;
    public const long CalibratedPerRecordBytes = 16 * 1024;

    public const long MinimumCalibratedRecordBytes = 320;
    public const long MaximumCalibratedRecordBytes = 262463;
    public const string CalibrationProfile = "linux-ext4-sqlite-3.53.3-queue9-v1";

    /// <summary>Use actual store metadata and the same rate-window size range for every accumulating target.
    /// A reference coefficient is not evidence that a different deployment/workload was calibrated.</summary>
    public static OutputCapacityEstimate EstimateForStore(TenonEdge.Abstractions.Contracts.DeliveryPolicy options,
        DeliveryQueueUsage target, DeliveryQueueUsage global, OutputTrafficSnapshot rates,
        IReadOnlyList<OutputTrafficSnapshot> accumulating, StorageProtectionStatus storage, DeliveryStoreInfo? store)
    {
        foreach (var sample in accumulating)
        {
            ValidateRate(sample.RecordsPerSecond);
            ValidateRate(sample.BytesPerSecond);
        }
        var calibration = SelectCalibration(storage.Space, store, accumulating);
        double? totalRate = rates.BytesPerSecond is not null && accumulating.All(item => item.BytesPerSecond is not null)
            ? accumulating.Sum(item => item.BytesPerSecond!.Value) : null;
        return Estimate(options, target, global, rates.RecordsPerSecond, rates.BytesPerSecond, totalRate,
            storage, calibration.Amplification) with
        { Calibration = calibration };
    }

    private static DiskCapacityCalibration SelectCalibration(StorageSpace? space, DeliveryStoreInfo? store,
        IReadOnlyList<OutputTrafficSnapshot> accumulating)
    {
        if (space?.FileSystem is null || store is null)
        { return new("environmentUnknown", null, null); }
        if (!OperatingSystem.IsLinux() || space.FileSystem != "ext4" || store.SqliteVersion != "3.53.3" ||
            store.SchemaVersion != 9 || store.JournalMode != "wal" || store.Synchronous != 2 ||
            store.PageSize != 4096 || store.WalAutoCheckpoint != 1000 || store.AutoVacuum != 0)
        { return new("unsupportedEnvironment", null, null); }
        if (accumulating.Count == 0 || accumulating.Any(item => item.BytesPerSecond is null || item.RecordsPerSecond is null ||
            item.BytesPerSecond > 0 && (item.MinimumRecordBytes is null || item.MaximumRecordBytes is null)))
        { return new("sampleUnknown", CalibrationProfile, null); }
        if (accumulating.Any(item => item.BytesPerSecond > 0 && (item.MinimumRecordBytes < MinimumCalibratedRecordBytes ||
            item.MaximumRecordBytes > MaximumCalibratedRecordBytes || item.MinimumRecordBytes > item.MaximumRecordBytes)))
        { return new("sampleOutsideProfile", CalibrationProfile, null); }
        var bytes = accumulating.Sum(item => item.BytesPerSecond!.Value);
        var records = accumulating.Sum(item => item.RecordsPerSecond!.Value);
        if (bytes == 0)
        { return new("sampleUnknown", CalibrationProfile, null); }
        var amplification = CalibratedBaseAmplification + CalibratedPerRecordBytes * records / bytes;
        return new("applicable", CalibrationProfile, amplification);
    }

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
