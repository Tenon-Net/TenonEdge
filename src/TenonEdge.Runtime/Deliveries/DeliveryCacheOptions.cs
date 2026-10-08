namespace TenonEdge.Runtime.Deliveries;

/// <summary>
/// 发送缓存 queue.db 的参数（总设计 §8.2、§8.4）。默认值是待实测调整的初始值。
/// </summary>
public sealed class DeliveryCacheOptions
{
    public const string FileName = "queue.db";

    /// <summary>queue.db 的绝对路径，位于数据目录中，与 config.db 分开（总设计 §8.2）。</summary>
    public required string DatabasePath { get; init; }

    /// <summary>所属配置库的标识（总设计 §10.2、§10.4）：新建 queue.db 时写入，供恢复备份后判断记录的来源。</summary>
    public required string ConfigStoreId { get; init; }

    /// <summary>分组提交的间隔：收到第一个请求后最多再等这么久，把期间到达的请求放进同一个事务（总设计 §8.2“50—100 毫秒”）。</summary>
    public TimeSpan CommitInterval { get; init; } = TimeSpan.FromMilliseconds(50);

    /// <summary>一个事务最多处理的记录数（插入与删除合计）；达到即提交，不再等待间隔。</summary>
    public int MaximumRecordsPerCommit { get; init; } = 1000;

    /// <summary>等待写者处理的请求数上限；满时提交请求的一方等待（背压），不在内存中无限堆积。</summary>
    public int RequestQueueCapacity { get; init; } = 1024;

    /// <summary>失败区独立条数上限；初始值，非容量承诺。</summary>
    public int MaximumFailedPerTarget { get; init; } = 100;
    public int MaximumFailedGlobal { get; init; } = 1000;
    public TimeSpan FailedRetention { get; init; } = TimeSpan.FromDays(7);

    public long MinimumDiskFreeBytes { get; init; } = 500_000_000;
    public double MinimumDiskFreeFraction { get; init; } = 0.05;

    public int MaximumPendingPerTarget { get; init; } = 10000;
    public long MaximumPendingBytesPerTarget { get; init; } = 64L * 1024 * 1024;
    public long MaximumQueueBytes { get; init; } = 256L * 1024 * 1024;
    public TimeSpan PendingRetention { get; init; } = TimeSpan.FromHours(24);

    /// <summary>检查参数，不合理时抛出异常。</summary>
    public DeliveryCacheOptions Validate()
    {
        if (string.IsNullOrWhiteSpace(DatabasePath) || !Path.IsPathFullyQualified(DatabasePath))
        {
            throw new ArgumentException("queue.db 的路径须为绝对路径。", nameof(DatabasePath));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(ConfigStoreId);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(CommitInterval, TimeSpan.Zero, nameof(CommitInterval));
        ArgumentOutOfRangeException.ThrowIfLessThan(MaximumRecordsPerCommit, 1, nameof(MaximumRecordsPerCommit));
        ArgumentOutOfRangeException.ThrowIfLessThan(RequestQueueCapacity, 1, nameof(RequestQueueCapacity));
        ArgumentOutOfRangeException.ThrowIfLessThan(MaximumFailedPerTarget, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaximumFailedGlobal, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(FailedRetention, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(FailedRetention, TimeSpan.FromDays(3650));
        ArgumentOutOfRangeException.ThrowIfLessThan(MaximumPendingPerTarget, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaximumPendingBytesPerTarget, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaximumQueueBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(PendingRetention, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(PendingRetention, TimeSpan.FromDays(3650));
        ArgumentOutOfRangeException.ThrowIfNegative(MinimumDiskFreeBytes);
        if (!double.IsFinite(MinimumDiskFreeFraction) || MinimumDiskFreeFraction is < 0 or >= 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MinimumDiskFreeFraction));
        }

        return this;
    }
}
