using System.Text.Json;
using SqlSugar;
using TenonEdge.Host.Features.Audit;
using TenonEdge.Host.Storage;
using TenonEdge.Runtime.Deliveries;

namespace TenonEdge.Host.Features.Deliveries;

/// <summary>执行尝试与开始/结果审计。两个库从不共享活跃事务；queue.db 完成记录是实际结果的唯一依据。
/// 不执行迁移、不负责权限/选择准备；形式 A 的等待意图仍须随配置事务另外登记。</summary>
internal sealed partial class DeliveryMigrationStore(ConfigDatabase database, GatewayIdentity identity, DeliveryCache queue, AuditTrail audit, TimeProvider time)
{
    public void Begin(DeliveryMigrationCommand command, long revision, string sourceSummary, bool sameOwnerConfirmed, string account, string? sourceIp)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Hold is not null)
        { throw new ArgumentException("意图执行须使用原子关联的开始入口。", nameof(command)); }
        BeginCore(command, revision, sourceSummary, sameOwnerConfirmed, account, sourceIp, null);
    }

    private void BeginCore(DeliveryMigrationCommand command, long revision, string sourceSummary, bool sameOwnerConfirmed,
        string account, string? sourceIp, DeliveryMigrationIntentRecord? intent)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentOutOfRangeException.ThrowIfLessThan(revision, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceSummary);
        ArgumentException.ThrowIfNullOrWhiteSpace(account);
        if (!sameOwnerConfirmed || command.ConfigStoreId != identity.ConfigStoreId)
        { throw new ArgumentException("迁移必须声明同一数据所有者，并属于当前配置库。"); }
        var row = new DeliveryMigrationRecord
        {
            OperationId = command.OperationId.ToString("D"),
            IntentId = intent?.IntentId,
            ConfigStoreId = command.ConfigStoreId,
            RequestHash = command.RequestHash,
            Mode = command.OldFingerprint is null ? "selected" : "oldTarget",
            Revision = revision,
            SourceConnectionId = command.SourceConnectionId,
            TargetConnectionId = command.TargetConnectionId,
            SourceSummary = sourceSummary,
            TargetSummary = command.Summary,
            RequestedRecords = command.Items?.Count,
            AccountName = account,
            SourceIp = sourceIp,
            StartedAt = time.GetUtcNow().ToUnixTimeMilliseconds(),
            Status = "started",
        };
        database.InTransaction(client =>
        {
            if (intent is not null)
            { BindIntentAttempt(client, intent, command, revision); }
            client.Insertable(row).ExecuteCommand();
            audit.Append(client, Entry(row, "delivery.migrationStarted", AuditResults.Success, intent is null
                ? "已确认同一数据所有者并开始迁移；尚无执行结果。"
                : "按配置提交时的所有者声明开始核对意图；当前目标仍须匹配已确认身份，尚无执行结果。"));
            return true;
        });
    }

    public DeliveryMigrationRecord? Read(Guid operationId)
    {
        using var client = database.CreateIsolatedClient();
        return client.Queryable<DeliveryMigrationRecord>().InSingle(operationId.ToString("D"));
    }

    /// <summary>只有运行时拥有的执行已结束才能调用；不能在取消 HTTP 等待后把后台执行误记为未执行。
    /// 即使上层报告失败，也优先采用队列完成记录。同步或审计失败保留 started，供下次启动补记。</summary>
    public DeliveryMigrationRecord Finish(Guid operationId, DeliveryWriteResult result) => FinishCore(operationId, result);

    /// <summary>仅在启动期间、任何迁移执行前调用。分页处理，不把全部历史读入内存；不重放迁移。</summary>
    public void RecoverInterrupted()
    {
        while (true)
        {
            List<DeliveryMigrationRecord> pending;
            using (var client = database.CreateIsolatedClient())
            { pending = client.Queryable<DeliveryMigrationRecord>().Where(row => row.Status == "started").OrderBy(row => row.StartedAt).Take(100).ToList(); }
            if (pending.Count == 0)
            { return; }
            foreach (var row in pending)
            { FinishCore(Guid.Parse(row.OperationId), null); }
        }
    }

    private DeliveryMigrationRecord FinishCore(Guid operationId, DeliveryWriteResult? result)
    {
        // 先读 queue.db，再开 config.db 短事务。调用方保证不会在此读取后继续提交同一尝试。
        var receipt = queue.ReadMigrationReceipt(operationId);
        return database.InTransaction(client =>
        {
            var row = client.Queryable<DeliveryMigrationRecord>().InSingle(operationId.ToString("D"))
                ?? throw new InvalidOperationException("迁移尚未持久登记开始。");
            if (row.Status != "started")
            { return row; }
            if (row.ConfigStoreId != identity.ConfigStoreId || receipt is not null &&
                (receipt.ConfigStoreId != row.ConfigStoreId || receipt.RequestHash != row.RequestHash ||
                    receipt.SourceConnectionId != row.SourceConnectionId || receipt.TargetConnectionId != row.TargetConnectionId ||
                    row.RequestedRecords is { } requested && receipt.AffectedRecords > requested))
            { throw new DeliveryStoreException("迁移开始记录与队列完成记录不一致，不能补记成功或重放；请检查配置库归属和存储。"); }
            if (receipt is null && result?.Committed == true)
            { throw new DeliveryStoreException("迁移报告已完成，但队列没有持久完成记录，不能补记成功。"); }
            row.Status = receipt is not null ? "completed" : result is null ? "interrupted" : "rejected";
            row.MigrationId = receipt?.MigrationId;
            row.AffectedRecords = receipt?.AffectedRecords ?? 0;
            row.CompletedAt = receipt?.CompletedAt.ToUnixTimeMilliseconds() ?? time.GetUtcNow().ToUnixTimeMilliseconds();
            row.ErrorCode = receipt is not null ? null : result is null ? "delivery.migrationInterrupted" : SafeError(result.Error);
            client.Updateable(row).ExecuteCommand();
            FinishIntent(client, row);
            audit.Append(client, Entry(row, "delivery.migrationFinished", receipt is null ? AuditResults.Failure : AuditResults.Success,
                receipt is not null ? $"已迁移{receipt.AffectedRecords}条；以队列持久完成记录为准，不代表已发送。"
                    : result is null ? "未执行：进程中断。" : "未执行：迁移检查或写入未完成。"));
            return row;
        });
    }

    private static string SafeError(string? error) => error switch
    {
        DeliveryMigrationErrors.Unavailable or DeliveryMigrationErrors.NotQuiescent or DeliveryMigrationErrors.TargetChanged or
        DeliveryMigrationErrors.ProtocolMismatch or DeliveryMigrationErrors.StoreChanged or DeliveryMigrationErrors.OperationChanged or
        DeliveryMigrationErrors.SelectionChanged or DeliveryMigrationErrors.QuotaExceeded or DeliveryMigrationErrors.StorageProtection => error,
        _ => "delivery.migrationExecutionFailed",
    };

    private static AuditEntry Entry(DeliveryMigrationRecord row, string action, string result, string message) =>
        new(action, result, row.AccountName, row.SourceIp, row.OperationId, JsonSerializer.Serialize(new
        {
            message,
            row.Mode,
            row.IntentId,
            row.Revision,
            row.SourceConnectionId,
            row.TargetConnectionId,
            row.SourceSummary,
            row.TargetSummary,
            sameOwnerConfirmed = true,
            row.RequestedRecords,
            row.AffectedRecords,
            row.MigrationId,
            row.ErrorCode,
        }));
}

[SugarTable("delivery_migration")]
internal sealed class DeliveryMigrationRecord
{
    [SugarColumn(ColumnName = "operation_id", IsPrimaryKey = true)] public string OperationId { get; set; } = string.Empty;
    [SugarColumn(ColumnName = "config_store_id")] public string ConfigStoreId { get; set; } = string.Empty;
    [SugarColumn(ColumnName = "intent_id")] public string? IntentId { get; set; }
    [SugarColumn(ColumnName = "request_hash")] public string RequestHash { get; set; } = string.Empty;
    [SugarColumn(ColumnName = "mode")] public string Mode { get; set; } = string.Empty;
    [SugarColumn(ColumnName = "revision")] public long Revision { get; set; }
    [SugarColumn(ColumnName = "source_connection_id")] public long SourceConnectionId { get; set; }
    [SugarColumn(ColumnName = "target_connection_id")] public long TargetConnectionId { get; set; }
    [SugarColumn(ColumnName = "source_summary")] public string SourceSummary { get; set; } = string.Empty;
    [SugarColumn(ColumnName = "target_summary")] public string TargetSummary { get; set; } = string.Empty;
    [SugarColumn(ColumnName = "requested_records")] public int? RequestedRecords { get; set; }
    [SugarColumn(ColumnName = "account_name")] public string AccountName { get; set; } = string.Empty;
    [SugarColumn(ColumnName = "source_ip")] public string? SourceIp { get; set; }
    [SugarColumn(ColumnName = "started_at")] public long StartedAt { get; set; }
    [SugarColumn(ColumnName = "status")] public string Status { get; set; } = string.Empty;
    [SugarColumn(ColumnName = "migration_id")] public long? MigrationId { get; set; }
    [SugarColumn(ColumnName = "affected_records")] public int? AffectedRecords { get; set; }
    [SugarColumn(ColumnName = "completed_at")] public long? CompletedAt { get; set; }
    [SugarColumn(ColumnName = "error_code")] public string? ErrorCode { get; set; }
}
