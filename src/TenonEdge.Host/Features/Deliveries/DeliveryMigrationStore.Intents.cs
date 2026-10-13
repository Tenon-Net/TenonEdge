using System.Text.Json;
using SqlSugar;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Host.Features.Audit;
using TenonEdge.Runtime.Deliveries;

namespace TenonEdge.Host.Features.Deliveries;

/// <summary>由已授权协调者捕获原身份并完成预检后构造；不是管理请求模型，不接受客户端指纹或摘要。</summary>
internal sealed record DeliveryMigrationIntentDraft(DeliveryMigrationHold Hold, OutputRecipientIdentity Source,
    string TargetRecipient, bool SameOwnerConfirmed);

internal sealed partial class DeliveryMigrationStore
{
    public DeliveryMigrationIntentRecord? ReadIntent(Guid intentId)
    {
        using var client = database.CreateIsolatedClient();
        return client.Queryable<DeliveryMigrationIntentRecord>().InSingle(intentId.ToString("D"));
    }

    // 以持久版本/连接排序，不依赖墙钟；等待意图不把活跃事务或整个历史留在内存。
    public DeliveryMigrationIntentRecord[] ReadWaitingIntents(long afterRevision = 0, long afterConnection = 0, int limit = 100)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(afterRevision, 0);
        ArgumentOutOfRangeException.ThrowIfLessThan(afterConnection, 0);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 100);
        using var client = database.CreateIsolatedClient();
        return [.. client.Queryable<DeliveryMigrationIntentRecord>()
            .Where(row => row.Status == "waiting" && row.ConfigStoreId == identity.ConfigStoreId &&
                (row.Revision > afterRevision || row.Revision == afterRevision && row.ConnectionId > afterConnection))
            .OrderBy(row => row.Revision).OrderBy(row => row.ConnectionId).Take(limit).ToList()];
    }

    // 仅 RuntimeVersionStore.Commit 在同一个 config.db 事务中调用；这里不访问 queue.db、不开始另一个事务。
    internal static void RegisterIntents(SqlSugarClient client, IReadOnlyList<DeliveryMigrationIntentDraft> intents,
        long revision, long sourceRevision, AuditEntry application, long now, AuditTrail trail)
    {
        foreach (var intent in intents.OrderBy(value => value.Hold.ConnectionId))
        {
            var row = new DeliveryMigrationIntentRecord
            {
                IntentId = intent.Hold.IntentId.ToString("D"),
                ConfigStoreId = intent.Hold.ConfigStoreId,
                Revision = revision,
                SourceRevision = sourceRevision,
                ConnectionId = intent.Hold.ConnectionId,
                Protocol = intent.Source.Protocol,
                OldFingerprint = intent.Hold.OldFingerprint,
                SourceSummary = intent.Source.Freeze(),
                TargetRecipient = intent.TargetRecipient,
                AccountName = application.AccountName!,
                SourceIp = application.SourceIp,
                RegisteredAt = now,
                Status = "waiting",
            };
            client.Insertable(row).ExecuteCommand();
            trail.Append(client, new("delivery.migrationIntentRegistered", AuditResults.Success, row.AccountName,
                row.SourceIp, row.IntentId, JsonSerializer.Serialize(new
                {
                    row.Revision,
                    row.SourceRevision,
                    row.ConnectionId,
                    row.SourceSummary,
                    row.TargetRecipient,
                    sameOwnerConfirmed = true,
                    message = "迁移意图随配置提交；等待隔离和目标身份，不代表已执行或已发送。",
                })));
        }
    }
}

[SugarTable("delivery_migration_intent")]
internal sealed class DeliveryMigrationIntentRecord
{
    [SugarColumn(ColumnName = "intent_id", IsPrimaryKey = true)] public string IntentId { get; set; } = string.Empty;
    [SugarColumn(ColumnName = "config_store_id")] public string ConfigStoreId { get; set; } = string.Empty;
    [SugarColumn(ColumnName = "revision")] public long Revision { get; set; }
    [SugarColumn(ColumnName = "source_revision")] public long SourceRevision { get; set; }
    [SugarColumn(ColumnName = "connection_id")] public long ConnectionId { get; set; }
    [SugarColumn(ColumnName = "protocol")] public string Protocol { get; set; } = string.Empty;
    [SugarColumn(ColumnName = "old_fingerprint")] public string OldFingerprint { get; set; } = string.Empty;
    [SugarColumn(ColumnName = "source_summary")] public string SourceSummary { get; set; } = string.Empty;
    [SugarColumn(ColumnName = "target_recipient")] public string TargetRecipient { get; set; } = string.Empty;
    [SugarColumn(ColumnName = "account_name")] public string AccountName { get; set; } = string.Empty;
    [SugarColumn(ColumnName = "source_ip")] public string? SourceIp { get; set; }
    [SugarColumn(ColumnName = "registered_at")] public long RegisteredAt { get; set; }
    [SugarColumn(ColumnName = "status")] public string Status { get; set; } = string.Empty;
    [SugarColumn(ColumnName = "last_operation_id")] public string? LastOperationId { get; set; }

    public DeliveryMigrationHold ToHold() => new(ConfigStoreId, Guid.Parse(IntentId), ConnectionId, OldFingerprint);
}
