namespace TenonEdge.Host.Features.Deliveries;

internal sealed record DeliveryMigrationIntentSummary(Guid Id, long Revision, long SourceRevision, long ConnectionId,
    string Status, string RegisteredBy, long RegisteredAt, Guid? LastOperationId, bool HasConfirmedTarget);
internal sealed record DeliveryMigrationIntentCursor(long Revision, long ConnectionId);
internal sealed record DeliveryMigrationIntentPage(DeliveryMigrationIntentSummary[] Items, DeliveryMigrationIntentCursor? Next);

internal sealed partial class DeliveryMigrationStore
{
    /// <summary>仅投影有界元数据；不把所有身份摘要/正文读到列表，不按会回拨的墙钟分页。
    /// 游标为原登记版本/连接。过滤结果是本次读到的状态，不声称多次分页之间冻结了整个历史。</summary>
    public DeliveryMigrationIntentPage ReadIntentPage(string? status, long? connectionId, long? afterRevision, long? afterConnection, int limit)
    {
        if (status is not (null or "waiting" or "rejected" or "completed") || connectionId is < 1 or > 9007199254740991L ||
            afterRevision is < 1 || afterConnection is < 1 or > 9007199254740991L ||
            (afterRevision is null) != (afterConnection is null) || limit is < 1 or > 100)
        { throw new ArgumentException("须提供有效状态、连接、完整游标和1至100的页大小。"); }
        using var client = database.CreateIsolatedClient();
        var rows = client.Queryable<DeliveryMigrationIntentRecord>()
            .Where(row => row.ConfigStoreId == identity.ConfigStoreId)
            .WhereIF(status is not null, row => row.Status == status)
            .WhereIF(connectionId is not null, row => row.ConnectionId == connectionId)
            .WhereIF(afterRevision is not null, row => row.Revision > afterRevision || row.Revision == afterRevision && row.ConnectionId > afterConnection)
            .OrderBy(row => row.Revision).OrderBy(row => row.ConnectionId).Take(limit + 1)
            .Select(row => new
            {
                row.IntentId,
                row.Revision,
                row.SourceRevision,
                row.ConnectionId,
                row.Status,
                row.AccountName,
                row.RegisteredAt,
                row.LastOperationId,
                HasConfirmedTarget = row.TargetFingerprint != null && row.TargetSummary != null
            })
            .ToList();
        var items = rows.Take(limit).Select(row => new DeliveryMigrationIntentSummary(Guid.Parse(row.IntentId), row.Revision, row.SourceRevision,
            row.ConnectionId, row.Status, row.AccountName, row.RegisteredAt, row.LastOperationId is { } id ? Guid.Parse(id) : null, row.HasConfirmedTarget)).ToArray();
        return new(items, rows.Count > limit ? new(items[^1].Revision, items[^1].ConnectionId) : null);
    }
}
