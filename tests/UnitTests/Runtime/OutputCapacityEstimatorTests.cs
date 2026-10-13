using TenonEdge.Runtime.Deliveries;
using TenonEdge.Runtime.Diagnostics;

namespace TenonEdge.UnitTests.Runtime;

public sealed class OutputCapacityEstimatorTests
{
    private static readonly DeliveryCacheOptions Options = new() { DatabasePath = "/unused/queue.db", ConfigStoreId = "test" };
    private static readonly DeliveryQueueUsage Empty = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
    private static readonly StorageProtectionStatus Space = new(false, false, new(100_000_000_000, 90_000_000_000));

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
