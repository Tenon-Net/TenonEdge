using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TenonEdge.Runtime.Deliveries;

/// <summary>已随配置 COMMIT 持久登记的形式 A 意图。此值本身不证明授权或提交，不能直接接受客户端自报。
/// 运行时先隔离旧指纹记录，待目标可用后另以执行尝试编号迁移；意图编号不随重试变化。</summary>
public sealed class DeliveryMigrationHold
{
    public DeliveryMigrationHold(string configStoreId, Guid intentId, long connectionId, string oldFingerprint)
    {
        if (!Guid.TryParseExact(configStoreId, "D", out var store) || store == Guid.Empty || intentId == Guid.Empty)
        { throw new ArgumentException("迁移意图或配置库编号无效。"); }
        ArgumentOutOfRangeException.ThrowIfLessThan(connectionId, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(connectionId, 9007199254740991L);
        ArgumentException.ThrowIfNullOrWhiteSpace(oldFingerprint);
        if (Encoding.UTF8.GetByteCount(oldFingerprint) > 1024)
        { throw new ArgumentException("原指纹超过上限。", nameof(oldFingerprint)); }
        ConfigStoreId = configStoreId;
        IntentId = intentId;
        ConnectionId = connectionId;
        OldFingerprint = oldFingerprint;
        RequestHash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        { Version = 1, ConfigStoreId, IntentId, ConnectionId, OldFingerprint })));
    }

    public string ConfigStoreId { get; }
    public Guid IntentId { get; }
    public long ConnectionId { get; }
    public string OldFingerprint { get; }
    public string RequestHash { get; }
}

public sealed record DeliveryMigrationHoldReceipt(string ConfigStoreId, Guid IntentId, string RequestHash,
    long ConnectionId, string OldFingerprint, int HeldRecords, DateTimeOffset CreatedAt);

internal sealed class HoldMigrationRequest(DeliveryMigrationHold command) : WriteRequest
{
    public DeliveryMigrationHold Command { get; } = command;
    public override int RecordCount => 1;
}
