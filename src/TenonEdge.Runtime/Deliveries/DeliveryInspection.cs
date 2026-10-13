using TenonEdge.Abstractions.Contracts;

namespace TenonEdge.Runtime.Deliveries;

/// <summary>故障记录筛选。时间为首次入队时间，左闭右开；原因是区分大小写的字面子串，不解释 SQL 通配符。</summary>
public sealed record DeliveryInspectionQuery(DeliveryState State, long? ConnectionId = null, string? DeviceKey = null,
    string? Reason = null, DateTimeOffset? From = null, DateTimeOffset? To = null, long After = 0, int Limit = 100)
{
    internal void Validate(bool allowPending = false)
    {
        if ((State is not (DeliveryState.Failed or DeliveryState.Quarantined) && !(allowPending && State == DeliveryState.Pending)) || ConnectionId is < 1 or > 9007199254740991L ||
            DeviceKey is { Length: > 128 } || Reason is { Length: > 1024 } || From >= To || After < 0 || Limit is < 1 or > 100)
        { throw new ArgumentException("故障记录查询参数无效。"); }
    }
}

/// <summary>只读元数据，不包含正文、目标地址、请求头、协议元数据或身份指纹；失效连接仍按冻结编号显示。</summary>
public sealed record DeliveryInspectionItem(long Sequence, Guid DeliveryId, long ConnectionId, string DeviceKey,
    long? RouteId, string? NodeLabel, long Revision, DateTimeOffset CreatedAt, DateTimeOffset? FailedAt,
    int FailureCount, DeliveryState State, string? QuarantineReason, string? LastError, long ByteSize, bool ClockAnomaly, long? MigrationId = null);

/// <summary>每页是独立短事务快照；翻页期间记录可能过期或被操作，不保证跨页快照或导出一致性。</summary>
public sealed record DeliveryInspectionPage(DateTimeOffset CapturedAt, DeliveryInspectionItem[] Items, long? NextSequence);

/// <summary>最多读取 64 KiB 原始正文。UTF-8 截断时舍去不完整末尾字符；非 UTF-8 前缀用 Base64 明示，不替换成乱码。</summary>
public sealed record DeliveryBodyPreview(Guid DeliveryId, DeliveryState State, string Encoding, string Content,
    long TotalBytes, int DisplayedBytes, bool Truncated, HttpResponseSummary? ResponseSummary = null);
