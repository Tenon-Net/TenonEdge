namespace TenonEdge.Runtime.Deliveries;

/// <summary>真实当前占用，不是安装以来累计入队数；失败区字节不占全局待发/隔离配额。</summary>
public sealed record DeliveryQueueUsage(long PendingCount, long PendingBytes, long FailedCount, long FailedBytes,
    long QuarantinedCount, long QuarantinedBytes, long ClockAnomalyCount,
    long TargetChangedCount, long TargetMissingCount, long ConfigStoreChangedCount, long UnknownQuarantineCount);
public sealed record DeliveryTargetQueueReport(long ConnectionId, DeliveryQueueUsage Usage,
    DeliveryQueuePolicyCounters Counters, long? HeadSequence, DateTimeOffset? HeadCreatedAt, bool HeadClockAnomaly);
public sealed record DeliveryQueuePolicyCounters(long QuotaRejected, long Expired, long Quarantined,
    long Rejected, long Poisoned, long FailureOverflowed, long FailureExpired);
public sealed record DeliveryQueueFiles(long DatabaseBytes, long WalBytes, long SharedMemoryBytes);
public sealed record DeliveryQueueReport(DateTimeOffset CapturedAt, DeliveryQueueUsage Usage,
    DeliveryQueuePolicyCounters Counters, DeliveryTargetQueueReport[] Targets, long? NextConnectionId, DeliveryQueueFiles? Files);
