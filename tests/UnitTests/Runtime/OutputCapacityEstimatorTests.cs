using TenonEdge.Runtime.Deliveries;
using TenonEdge.Runtime.Diagnostics;

namespace TenonEdge.UnitTests.Runtime;

public sealed class OutputCapacityEstimatorTests
{
    private static readonly DeliveryCacheOptions Options = new() { DatabasePath = "/unused/queue.db", ConfigStoreId = "test" };
    private static readonly DeliveryQueueUsage Empty = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
    private static readonly StorageProtectionStatus Space = new(false, false, new(100_000_000_000, 90_000_000_000));

    private static readonly DeliveryStoreInfo CalibratedStore = new("/not-read/queue.db", "3.53.3", "wal", 2, 9, "test", 4096, 1000, 0);
    private static readonly OutputTrafficSnapshot Traffic = new(1, [], 2, 11000, 10, true, 320, 262463);

    [Theory]
    [InlineData(null, "environmentUnknown")]
    [InlineData("tmpfs", "unsupportedEnvironment")]
    [InlineData("overlay", "unsupportedEnvironment")]
    [InlineData("btrfs", "unsupportedEnvironment")]
    public void AnUnverifiedFilesystemCannotAcquireAFullEstimate(string? filesystem, string reason)
    {
        var estimate = EstimateStore(filesystem);
        Assert.Null(estimate.Seconds);
        Assert.Equal(5000, estimate.QuotaUpperBoundSeconds);
        Assert.Equal("uncalibrated", estimate.Constraints[3].Status);
        Assert.Equal(reason, estimate.Calibration!.Status);
        Assert.Null(estimate.Calibration.Amplification);
    }

    [Fact]
    public void SupportedStoreAndObservedRecordRangeExposeAnExplicitEmpiricalProfile()
    {
        var estimate = EstimateStore("ext4");
        if (!OperatingSystem.IsLinux())
        { Assert.Equal("unsupportedEnvironment", estimate.Calibration!.Status); return; }
        Assert.NotNull(estimate.Seconds);
        Assert.Equal("applicable", estimate.Calibration!.Status);
        Assert.Equal(OutputCapacityEstimator.CalibrationProfile, estimate.Calibration.Profile);
        Assert.Equal(OutputCapacityEstimator.CalibratedBaseAmplification + OutputCapacityEstimator.CalibratedPerRecordBytes * 2.0 / 11000,
            estimate.Calibration.Amplification);
    }

    [Fact]
    public void NativeVersionSchemaAndActualPragmasMustMatchTheMeasurement()
    {
        foreach (var store in new[] {
            CalibratedStore with { SqliteVersion = "3.51.3" }, CalibratedStore with { SchemaVersion = 10 },
            CalibratedStore with { JournalMode = "delete" }, CalibratedStore with { Synchronous = 1 },
            CalibratedStore with { PageSize = 8192 }, CalibratedStore with { WalAutoCheckpoint = 0 },
            CalibratedStore with { AutoVacuum = 1 } })
        {
            var estimate = EstimateStore("ext4", store);
            Assert.Null(estimate.Seconds);
            Assert.Equal("unsupportedEnvironment", estimate.Calibration!.Status);
        }
    }

    [Theory]
    [InlineData(null, null, "sampleUnknown")]
    [InlineData(319L, 1000L, "sampleOutsideProfile")]
    [InlineData(1000L, 262464L, "sampleOutsideProfile")]
    [InlineData(1500L, 1000L, "sampleOutsideProfile")]
    public void AnyAccumulatingTargetOutsideTheProfileCannotHideBehindItsAverage(long? minimum, long? maximum, string reason)
    {
        // The selected target is normal, but another accumulating target is not.
        var other = Traffic with { ConnectionId = 2, MinimumRecordBytes = minimum, MaximumRecordBytes = maximum };
        var estimate = EstimateStore("ext4", accumulating: [Traffic, other]);
        Assert.Null(estimate.Seconds);
        Assert.Equal(OperatingSystem.IsLinux() ? reason : "unsupportedEnvironment", estimate.Calibration!.Status);
        Assert.Equal("uncalibrated", estimate.Constraints[3].Status);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0.0)]
    public void MissingOrEmptyRateWindowsDoNotClaimToBeCalibrated(double? rate)
    {
        var empty = Traffic with { RecordsPerSecond = rate, BytesPerSecond = rate, MinimumRecordBytes = null, MaximumRecordBytes = null };
        var estimate = OutputCapacityEstimator.EstimateForStore(Options.ToPolicy(), Empty, Empty, empty, [empty],
            Space with { Space = Space.Space! with { FileSystem = "ext4" } }, CalibratedStore);
        Assert.Null(estimate.Seconds);
        Assert.Null(estimate.QuotaUpperBoundSeconds);
        Assert.Null(estimate.Calibration!.Amplification);
        Assert.Equal(OperatingSystem.IsLinux() ? "sampleUnknown" : "unsupportedEnvironment", estimate.Calibration.Status);
    }

    [Fact]
    public void MixedTargetsUseTheSumOfRecordAndByteRatesNotTheSelectedTargetsMeanSize()
    {
        var other = Traffic with { ConnectionId = 2, RecordsPerSecond = 10, BytesPerSecond = 3200, MinimumRecordBytes = 320, MaximumRecordBytes = 320 };
        var estimate = EstimateStore("ext4", accumulating: [Traffic, other]);
        if (!OperatingSystem.IsLinux())
        { Assert.Null(estimate.Calibration!.Amplification); return; }
        var expected = 2.3 + 16384.0 * 12 / 14200;
        Assert.Equal(expected, estimate.Calibration!.Amplification);
        Assert.Equal((Space.Space!.AvailableBytes - Space.Space.TotalBytes * Options.MinimumDiskFreeFraction) / (14200 * expected),
            estimate.Constraints.Single(item => item.Kind == "disk").Seconds);
    }

    [Fact]
    public void UnknownCalibrationDoesNotHideAnAlreadyExhaustedDiskOrQuota()
    {
        var disk = OutputCapacityEstimator.EstimateForStore(Options.ToPolicy(), Empty, Empty, Traffic, [Traffic],
            new(true, false, new(1_000_000_000, 400_000_000, "tmpfs")), CalibratedStore);
        Assert.Equal(0, disk.Seconds);
        Assert.Equal("disk", disk.LimitingConstraint);
        Assert.Equal("unsupportedEnvironment", disk.Calibration!.Status);
        var quota = OutputCapacityEstimator.EstimateForStore(Options.ToPolicy(), Empty with { PendingCount = Options.MaximumPendingPerTarget },
            Empty, Traffic, [Traffic], new(true, true, null), null);
        Assert.Equal(0, quota.Seconds);
        Assert.Equal("targetCount", quota.LimitingConstraint);
        Assert.Equal("environmentUnknown", quota.Calibration!.Status);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1.0)]
    public void InvalidRatesOnAnotherTargetCannotBecomeAValidAggregate(double rate)
    {
        var other = Traffic with { ConnectionId = 2, RecordsPerSecond = rate };
        Assert.Throws<ArgumentOutOfRangeException>(() => EstimateStore("ext4", accumulating: [Traffic, other]));
        other = Traffic with { ConnectionId = 2, BytesPerSecond = rate };
        Assert.Throws<ArgumentOutOfRangeException>(() => EstimateStore("ext4", accumulating: [Traffic, other]));
    }

    private static OutputCapacityEstimate EstimateStore(string? filesystem, DeliveryStoreInfo? store = null,
        IReadOnlyList<OutputTrafficSnapshot>? accumulating = null) => OutputCapacityEstimator.EstimateForStore(
            Options.ToPolicy(), Empty, Empty, Traffic, accumulating ?? [Traffic],
            Space with { Space = Space.Space! with { FileSystem = filesystem } }, store ?? CalibratedStore);

    [Fact]
    public void DesignExampleIsCountBoundButUncalibratedDiskIsNotACompleteEstimate()
    {
        var estimate = OutputCapacityEstimator.Estimate(Options, Empty, Empty, 2, 11000, 11000, Space);
        Assert.Null(estimate.Seconds);
        Assert.Null(estimate.LimitingConstraint);
        Assert.Equal(5000, estimate.QuotaUpperBoundSeconds);
        Assert.Equal("targetCount", estimate.QuotaLimitingConstraint);
        Assert.Equal("uncalibrated", estimate.Constraints[3].Status);
        var calibrated = OutputCapacityEstimator.Estimate(Options, Empty, Empty, 2, 11000, 11000, Space, 3);
        Assert.Equal(5000, calibrated.Seconds);
        Assert.Equal("targetCount", calibrated.LimitingConstraint);
        Assert.NotEqual(Options.PendingRetention.TotalSeconds, calibrated.Seconds);
    }

    [Fact]
    public void ActualOccupancyAndOtherAccumulatingTargetsDetermineTheLimitingQuota()
    {
        var target = Empty with { PendingCount = 9990, PendingBytes = Options.MaximumPendingBytesPerTarget - 1000 };
        var global = Empty with { PendingBytes = 500, QuarantinedBytes = Options.MaximumQueueBytes - 1000, FailedBytes = long.MaxValue };
        var estimate = OutputCapacityEstimator.Estimate(Options, target, global, 1, 100, 1000, Space, 2);
        Assert.Equal(10, estimate.Constraints[0].Seconds);
        Assert.Equal(10, estimate.Constraints[1].Seconds);
        Assert.Equal(.5, estimate.Seconds);
        Assert.Equal("globalBytes", estimate.LimitingConstraint); // 失败区另计，隔离区占全局配额。
    }

    [Fact]
    public void DiskReserveUsesLargerThresholdAndNeverUsesCurrentFileSizeAsAmplification()
    {
        var estimate = OutputCapacityEstimator.Estimate(Options, Empty, Empty, 1, 1000, 1000,
            new(false, false, new(100_000_000_000, 5_000_002_000)), 2);
        Assert.Equal(1, estimate.Seconds);
        Assert.Equal("disk", estimate.LimitingConstraint);
        Assert.Equal(0, OutputCapacityEstimator.Estimate(Options, Empty, Empty, null, null, null,
            new(true, false, new(1_000_000_000, 400_000_000))).Seconds);
    }

    [Fact]
    public void UnknownStaleOrZeroRatesAreNotInfiniteCapacityAndExhaustionIsExplicit()
    {
        var unknown = OutputCapacityEstimator.Estimate(Options, Empty, Empty, null, null, null, new(true, true, null));
        Assert.Null(unknown.Seconds);
        Assert.Null(unknown.QuotaUpperBoundSeconds);
        Assert.Equal("rateUnknown", unknown.Constraints[0].Status);
        Assert.Equal("spaceUnknown", unknown.Constraints[3].Status);
        var zero = OutputCapacityEstimator.Estimate(Options, Empty, Empty, 0, 0, 0, Space, 2);
        Assert.Null(zero.Seconds);
        Assert.All(zero.Constraints, item => Assert.Equal("noGrowth", item.Status));
        var full = OutputCapacityEstimator.Estimate(Options, Empty with { PendingCount = 10001 }, Empty, null, null, null, Space);
        Assert.Equal(0, full.Seconds);
        Assert.Equal("exhausted", full.Constraints[0].Status);
        Assert.Throws<ArgumentOutOfRangeException>(() => OutputCapacityEstimator.Estimate(Options, Empty, Empty, double.NaN, 1, 1, Space));
        Assert.Throws<ArgumentOutOfRangeException>(() => OutputCapacityEstimator.Estimate(Options, Empty, Empty, 1, 1, 1, Space, double.PositiveInfinity));
    }
}
