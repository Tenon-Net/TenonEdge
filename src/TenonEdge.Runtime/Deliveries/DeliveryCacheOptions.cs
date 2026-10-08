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
        return this;
    }
}
