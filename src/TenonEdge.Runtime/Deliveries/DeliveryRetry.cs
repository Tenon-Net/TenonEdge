namespace TenonEdge.Runtime.Deliveries;

/// <summary>投递编号不变；seq 是本次选择的队列代次。必须同时匹配，不能仅按稳定编号重放人工重试。</summary>
public sealed record DeliveryRetryItem(long Sequence, Guid DeliveryId);

public static class DeliveryRetryErrors
{
    public const string SelectionChanged = "delivery.retrySelectionChanged";
    public const string TargetChanged = "delivery.retryTargetChanged";
    public const string QuotaExceeded = "delivery.retryQuotaExceeded";
    public const string StorageProtection = "delivery.retryStorageProtection";
    public const string SequenceExhausted = "delivery.retrySequenceExhausted";
}

internal sealed class RetryFailedRequest : WriteRequest
{
    // 一次管理写入有界；当前默认全局失败区1000条。更大配置由页面明确分批选择，禁止后台隐式拆批。
    public const int MaximumItems = 1000;
    public RetryFailedRequest(IReadOnlyList<DeliveryRetryItem> items, IReadOnlyDictionary<long, string> targetFingerprints)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(targetFingerprints);
        if (items.Count is < 1 or > MaximumItems || items.Any(item => item is null || item.Sequence < 1 || item.DeliveryId == Guid.Empty) ||
            items.Select(item => item.Sequence).Distinct().Count() != items.Count ||
            items.Select(item => item.DeliveryId).Distinct().Count() != items.Count ||
            targetFingerprints.Count is < 1 or > 500 || targetFingerprints.Any(pair => pair.Key is < 1 or > 9007199254740991L ||
                string.IsNullOrEmpty(pair.Value) || pair.Value.Length > 1024))
        { throw new ArgumentException("失败记录重试的选择或目标快照无效。"); }
        Items = items.OrderBy(item => item.Sequence).ToArray();
        TargetFingerprints = new Dictionary<long, string>(targetFingerprints);
    }

    public IReadOnlyList<DeliveryRetryItem> Items { get; }
    public IReadOnlyDictionary<long, string> TargetFingerprints { get; }
    public override int RecordCount => Items.Count;
}
