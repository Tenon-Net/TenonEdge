namespace TenonEdge.Runtime.Deliveries;

/// <summary>queue.db 中一条已提交的投递记录（总设计 §8.2、§10.2 Delivery）。</summary>
public sealed record StoredDelivery(
    long Seq,
    Guid DeliveryId,
    long ConnectionId,
    Guid SourceMessageId,
    string DeviceKey,
    long? RouteId,
    string? NodeLabel,
    long Revision,
    ReadOnlyMemory<byte> Payload,
    string? TargetAddress,
    int? QualityOfService,
    IReadOnlyDictionary<string, string>? Headers,
    string Fingerprint,
    string FingerprintSummary,
    DateTimeOffset CreatedAt,
    int FailureCount,
    DeliveryState State,
    string? QuarantineReason,
    string? LastError,
    long? MigrationId);

/// <summary>投递记录的状态（总设计 §8.2）：待发、失败区、隔离。</summary>
public enum DeliveryState
{
    Pending,
    Failed,
    Quarantined,
}

/// <summary>写入请求的结果：数据拒绝可以计数后丢弃；存储失败没有接管，调用方可以重试。</summary>
public enum DeliveryWriteStatus
{
    Committed,
    Rejected,
    StorageFailed,
    Stopped,
}

/// <summary>只有包含本请求的分组提交返回之后，结果才为 Committed（总设计 §8.5）。</summary>
public sealed record DeliveryWriteResult(DeliveryWriteStatus Status, IReadOnlyList<long> Seqs, int AffectedRecords, string? Error)
{
    public bool Committed => Status == DeliveryWriteStatus.Committed;

    internal static DeliveryWriteResult Inserted(IReadOnlyList<long> seqs) => new(DeliveryWriteStatus.Committed, seqs, seqs.Count, null);

    internal static DeliveryWriteResult Deleted(int count) => new(DeliveryWriteStatus.Committed, [], count, null);

    internal static DeliveryWriteResult Failed(string error) => new(DeliveryWriteStatus.StorageFailed, [], 0, error);

    internal static DeliveryWriteResult Rejected(string error) => new(DeliveryWriteStatus.Rejected, [], 0, error);

    internal static DeliveryWriteResult Stopped(string error) => new(DeliveryWriteStatus.Stopped, [], 0, error);
}

/// <summary>
/// 发送缓存的状态快照（总设计 §8.2、§8.5）；计数只在内存中，重启后从零开始，持久化在 T1-07。
/// FailedRecords 是因存储异常（回滚、打不开）没有写入的记录；RejectedRecords 是因数据错误（违反约束）被拒绝的记录。
/// </summary>
public sealed record DeliveryCacheStatus(
    bool StorageFault,
    string? LastError,
    DateTimeOffset? LastErrorAt,
    long CommittedBatches,
    long CommittedRecords,
    long DeletedRecords,
    long FailedBatches,
    long FailedRecords,
    long RejectedRecords,
    DeliveryStoreInfo? Store);

/// <summary>queue.db 打开时回读的信息（V-07）：实际加载的 SQLite 版本与持久化设置。</summary>
public sealed record DeliveryStoreInfo(
    string FilePath,
    string SqliteVersion,
    string JournalMode,
    long Synchronous,
    int SchemaVersion,
    string ConfigStoreId);
