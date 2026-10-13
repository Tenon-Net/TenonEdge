namespace TenonEdge.Runtime.Deliveries;

public sealed record DeliveryMigrationKey(long Sequence, Guid DeliveryId, long? MigrationId);

/// <summary>只读迁移身份元数据，不读取正文、请求头或协议成功规则。</summary>
public sealed record DeliveryMigrationCandidate(DeliveryMigrationItem Item, string Summary, DeliveryState State, DateTimeOffset CreatedAt);
public sealed record DeliveryMigrationPreflight(int Count, string? Error, DeliveryMigrationQuotaDeficit? Quota);
public sealed class DeliveryMigrationSelectionException(string code) : InvalidOperationException(code)
{
    public string Code { get; } = code;
}
