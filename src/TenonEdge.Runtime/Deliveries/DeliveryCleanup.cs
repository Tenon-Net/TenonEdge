namespace TenonEdge.Runtime.Deliveries;

/// <summary>清理确认绑定原编号、seq、目标及迁移代次；迁移保留seq，不能让旧确认删除迁移后的记录。</summary>
public sealed record DeliveryCleanupItem(long Sequence, Guid DeliveryId, long ConnectionId, long? MigrationId);

/// <summary>只允许清理失败/隔离区的一批明确选择，不在执行时重新套用筛选，也不隐式分批。</summary>
public sealed class DeliveryCleanupSelection
{
    public const int MaximumItems = 1000;

    public DeliveryCleanupSelection(DeliveryState state, IReadOnlyList<DeliveryCleanupItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (state is not (DeliveryState.Failed or DeliveryState.Quarantined) || items.Count is < 1 or > MaximumItems)
        { throw new ArgumentException("清理区域或选择数量无效。"); }
        var snapshot = items.ToArray();
        if (snapshot.Any(item => item is null || item.Sequence < 1 || item.DeliveryId == Guid.Empty ||
                item.ConnectionId is < 1 or > 9007199254740991L || item.MigrationId is < 1) ||
            snapshot.Select(item => item.Sequence).Distinct().Count() != snapshot.Length ||
            snapshot.Select(item => item.DeliveryId).Distinct().Count() != snapshot.Length)
        { throw new ArgumentException("清理记录的编号、代次或目标无效。"); }
        State = state;
        Items = Array.AsReadOnly(snapshot.OrderBy(item => item.Sequence).ToArray());
    }

    public DeliveryState State { get; }
    public IReadOnlyList<DeliveryCleanupItem> Items { get; }
}

public static class DeliveryCleanupErrors
{
    public const string SelectionChanged = "delivery.cleanupSelectionChanged";
}

internal sealed class CleanupRequest(DeliveryCleanupSelection selection) : WriteRequest
{
    public DeliveryCleanupSelection Selection { get; } = selection;
    public override int RecordCount => Selection.Items.Count;
}
