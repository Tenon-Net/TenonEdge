using System.Security.Cryptography;
using System.Text.Json;

namespace TenonEdge.Runtime.Deliveries;

// 宿主必须先校验有效配置/每条冻结记录的协议/所有者声明并持久登记开始，再交给 RuntimeHost 的协调入口。
// DeliveryCache 的实际写入仍为 internal，不能从管理 API 绕过发送者暂停。
public sealed record DeliveryMigrationItem(long Sequence, Guid DeliveryId, string Fingerprint, long? MigrationId);

public sealed record DeliveryMigrationReceipt(long MigrationId, Guid OperationId, string ConfigStoreId,
    string RequestHash, long SourceConnectionId, long TargetConnectionId, int AffectedRecords, DateTimeOffset CompletedAt);

public sealed record DeliveryMigrationQuotaDeficit(long PendingRecords, long PendingBytes, long GlobalBytes, long FailedRecords)
{
    public bool Exceeded => PendingRecords > 0 || PendingBytes > 0 || GlobalBytes > 0 || FailedRecords > 0;
}

public static class DeliveryMigrationErrors
{
    public const string Unavailable = "delivery.migrationUnavailable";
    public const string NotQuiescent = "delivery.migrationNotQuiescent";
    public const string TargetChanged = "delivery.migrationTargetChanged";
    public const string ProtocolMismatch = "delivery.migrationProtocolMismatch";
    public const string StoreChanged = "delivery.migrationStoreChanged";
    public const string OperationChanged = "delivery.migrationOperationChanged";
    public const string SelectionChanged = "delivery.migrationSelectionChanged";
    public const string QuotaExceeded = "delivery.migrationQuotaExceeded";
    public const string StorageProtection = "delivery.migrationStorageProtection";
}

public sealed class DeliveryMigrationCommand
{
    private DeliveryMigrationCommand(string configStoreId, Guid operationId, long sourceConnectionId, long targetConnectionId,
        string fingerprint, string summary, string? oldFingerprint, IReadOnlyList<DeliveryMigrationItem>? items)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configStoreId);
        if (operationId == Guid.Empty)
        { throw new ArgumentException("迁移操作编号不能为空。", nameof(operationId)); }
        ArgumentOutOfRangeException.ThrowIfLessThan(sourceConnectionId, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(sourceConnectionId, 9007199254740991L);
        ArgumentOutOfRangeException.ThrowIfLessThan(targetConnectionId, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(targetConnectionId, 9007199254740991L);
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(summary);
        if (items is not null && (items.Count == 0 || items.Any(item => item is null || item.Sequence < 1 ||
            item.DeliveryId == Guid.Empty || string.IsNullOrWhiteSpace(item.Fingerprint) || item.MigrationId is < 1) ||
            items.Select(item => item.Sequence).Distinct().Count() != items.Count ||
            items.Select(item => item.DeliveryId).Distinct().Count() != items.Count))
        { throw new ArgumentException("迁移记录选择无效。", nameof(items)); }
        ConfigStoreId = configStoreId;
        OperationId = operationId;
        SourceConnectionId = sourceConnectionId;
        TargetConnectionId = targetConnectionId;
        Fingerprint = fingerprint;
        Summary = summary;
        OldFingerprint = oldFingerprint;
        Items = items is null ? null : Array.AsReadOnly(items.OrderBy(item => item.Sequence).ToArray());
        // 规范化元数据，不含报文/秘密。相同操作编号只能重放同一命令，不能扩大选择或改接收方。
        RequestHash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        { Version = 1, ConfigStoreId, OperationId, SourceConnectionId, TargetConnectionId, Fingerprint, Summary, OldFingerprint, Items })));
    }

    // B：调用方负责有界选择；这里不截断、不隐式拆批。允许发送结果落库后的状态改变，不允许换代。
    public static DeliveryMigrationCommand Selected(string configStoreId, Guid operationId, long sourceConnectionId,
        long targetConnectionId, string fingerprint, string summary, IReadOnlyList<DeliveryMigrationItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return new(configStoreId, operationId, sourceConnectionId, targetConnectionId, fingerprint, summary, null, items);
    }

    // A：旧指纹全范围由 SQLite 选择，不受管理列表分页限制，不把整个范围/正文读入托管内存。
    public static DeliveryMigrationCommand OldTarget(string configStoreId, Guid operationId, long connectionId,
        string oldFingerprint, string fingerprint, string summary)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(oldFingerprint);
        return new(configStoreId, operationId, connectionId, connectionId, fingerprint, summary, oldFingerprint, null);
    }

    public string ConfigStoreId { get; }
    public Guid OperationId { get; }
    public long SourceConnectionId { get; }
    public long TargetConnectionId { get; }
    public string Fingerprint { get; }
    public string Summary { get; }
    public string? OldFingerprint { get; }
    public IReadOnlyList<DeliveryMigrationItem>? Items { get; }
    public string RequestHash { get; }
}

internal sealed class MigrateDeliveriesRequest(DeliveryMigrationCommand command) : WriteRequest
{
    public DeliveryMigrationCommand Command { get; } = command;
    public override int RecordCount => Command.Items?.Count ?? 1;
}
