namespace TenonEdge.Runtime.Deliveries;

/// <summary>导出整个筛选范围，不沿用列表的页号或单页上限；时间、原因和设备匹配口径与查看一致。</summary>
public sealed record DeliveryExportFilter(DeliveryState State, long? ConnectionId = null, string? DeviceKey = null,
    string? Reason = null, DateTimeOffset? From = null, DateTimeOffset? To = null)
{
    internal DeliveryInspectionQuery Query => new(State, ConnectionId, DeviceKey, Reason, From, To);

    public void Validate() => Query.Validate();
}

/// <summary>只含核对信息的不可变凭据，由原缓存生成并在服务端保留；不是客户端可以自行构造的授权令牌。
/// 新增 seq 不进入本次导出；已核对记录在读取前发生清理、重试、迁移或元数据变化时拒绝整份文件。
/// 正文在队列中冻结不变。此凭据不复制正文、不持有数据库连接，也不跨进程恢复。</summary>
public sealed class DeliveryExportReview
{
    internal DeliveryExportReview(DeliveryReader owner, DeliveryExportFilter filter, DateTimeOffset capturedAt,
        long throughSequence, long count, long payloadBytes, long maximumFileBytes,
        DateTimeOffset? firstCreatedAt, DateTimeOffset? lastCreatedAt, string selectionHash)
    {
        Owner = owner;
        Filter = filter;
        CapturedAt = capturedAt;
        ThroughSequence = throughSequence;
        Count = count;
        PayloadBytes = payloadBytes;
        MaximumFileBytes = maximumFileBytes;
        FirstCreatedAt = firstCreatedAt;
        LastCreatedAt = lastCreatedAt;
        SelectionHash = selectionHash;
    }

    internal DeliveryReader Owner { get; }
    internal string SelectionHash { get; }
    public DeliveryExportFilter Filter { get; }
    public DateTimeOffset CapturedAt { get; }
    public long ThroughSequence { get; }
    public long Count { get; }
    public long PayloadBytes { get; }
    /// <summary>包含 JSON 转义最坏膨胀的文件字节上界，不是磁盘剩余量或可导出的保证。</summary>
    public long MaximumFileBytes { get; }
    public DateTimeOffset? FirstCreatedAt { get; }
    public DateTimeOffset? LastCreatedAt { get; }
}
