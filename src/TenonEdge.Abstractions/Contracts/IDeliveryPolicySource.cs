namespace TenonEdge.Abstractions.Contracts;

/// <summary>配置库提供的不可变队列运行参数，不进入运行计划。写事务/过期清理在开始前持有读取租约，直至COMMIT完成；租约内的快照不会被替换。</summary>
public interface IDeliveryPolicySource
{
    DeliveryPolicy Read();
    /// <summary>同步、同线程释放；必须在打开事务之前取得，不跨await或请求保存。</summary>
    IDisposable AcquireRead();
}

public sealed record DeliveryPolicy
{
    public int MaximumPendingPerTarget { get; init; } = 10000;
    public long MaximumPendingBytesPerTarget { get; init; } = 64L * 1024 * 1024;
    public long MaximumQueueBytes { get; init; } = 256L * 1024 * 1024;
    public TimeSpan PendingRetention { get; init; } = TimeSpan.FromHours(24);
    public int MaximumFailedPerTarget { get; init; } = 100;
    public int MaximumFailedGlobal { get; init; } = 1000;
    public TimeSpan FailedRetention { get; init; } = TimeSpan.FromDays(7);
    public long MinimumDiskFreeBytes { get; init; } = 500_000_000;
    public double MinimumDiskFreeFraction { get; init; } = 0.05;
}
