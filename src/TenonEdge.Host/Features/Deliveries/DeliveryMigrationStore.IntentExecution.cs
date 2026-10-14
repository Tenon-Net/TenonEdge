using SqlSugar;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Host.Features.Audit;
using TenonEdge.Host.Storage;
using TenonEdge.Runtime.Deliveries;

namespace TenonEdge.Host.Features.Deliveries;

internal sealed partial class DeliveryMigrationStore
{
    /// <summary>每连接只取最早等待意图；未同步的尝试仍挡住同连接后续意图，但不阻塞其他连接。
    /// 按持久登记顺序分页，协调者推进有界内存游标，不能让第一页不可用的目标饿死后页。旧版缺少新身份的意图不自动补造确认。</summary>
    public DeliveryMigrationIntentRecord[] ReadExecutableIntents(long afterRevision = 0, long afterConnection = 0)
    {
        using var client = database.CreateIsolatedClient();
        return [.. client.Queryable<DeliveryMigrationIntentRecord>()
            .Where(row => row.ConfigStoreId == identity.ConfigStoreId && row.Status == "waiting" &&
                row.TargetFingerprint != null && row.TargetSummary != null &&
                (row.Revision > afterRevision || row.Revision == afterRevision && row.ConnectionId > afterConnection) &&
                !SqlFunc.Subqueryable<DeliveryMigrationIntentRecord>().Where(older => older.ConfigStoreId == row.ConfigStoreId &&
                    older.ConnectionId == row.ConnectionId && older.Status == "waiting" && older.Revision < row.Revision).Any() &&
                !SqlFunc.Subqueryable<DeliveryMigrationRecord>().Where(attempt => attempt.OperationId == row.LastOperationId && attempt.Status == "started").Any())
            .OrderBy(row => row.Revision).OrderBy(row => row.ConnectionId).Take(100).ToList()];
    }

    public void BeginIntent(DeliveryMigrationCommand command, long revision)
    {
        ArgumentNullException.ThrowIfNull(command);
        var hold = command.Hold ?? throw new ArgumentException("须提供意图限定的迁移命令。", nameof(command));
        var intent = ReadIntent(hold.IntentId) ?? throw new InvalidOperationException("迁移意图不存在。");
        var actor = ReadIntentActor(intent);
        BeginCore(command, revision, intent.SourceSummary, true, actor.Account, actor.SourceIp, intent);
    }

    // 显式重试之后的进程中断续跑，沿用最近一次已确认尝试的完整身份，不倒退为首次登记人。
    // 尤其不能用 previous.TokenName ?? intent.TokenName：Cookie 管理员接替令牌审批时 null 是真实身份。
    internal (AuditAccount Account, string? SourceIp) ReadIntentActor(DeliveryMigrationIntentRecord intent)
    {
        var previous = intent.LastOperationId is { } id ? Read(Guid.Parse(id)) : null;
        return previous is null ? (new(intent.AccountName, intent.TokenName), intent.SourceIp)
            : (new(previous.AccountName, previous.TokenName), previous.SourceIp);
    }

    private static void BindIntentAttempt(SqlSugarClient client, DeliveryMigrationIntentRecord expected,
        DeliveryMigrationCommand command, long revision, bool retry)
    {
        var intent = client.Queryable<DeliveryMigrationIntentRecord>().InSingle(expected.IntentId)
            ?? throw new InvalidOperationException("迁移意图不存在。");
        if ((!retry && intent.Status != "waiting") || revision < intent.Revision || intent.ToHold().RequestHash != command.Hold?.RequestHash ||
            (!retry && (command.Fingerprint != intent.TargetFingerprint || command.Summary != intent.TargetSummary)) ||
            intent.SourceSummary != expected.SourceSummary || intent.AccountName != expected.AccountName || intent.TokenName != expected.TokenName || intent.SourceIp != expected.SourceIp ||
            client.Queryable<DeliveryMigrationIntentRecord>().Any(row => row.ConfigStoreId == intent.ConfigStoreId &&
                row.ConnectionId == intent.ConnectionId && row.Status == "waiting" && row.Revision < intent.Revision))
        { throw new InvalidOperationException("意图已变化或同连接仍有更早的等待意图。"); }
        if (retry)
        {
            if (!MatchesRetryReview(intent, expected) ||
                client.Queryable<SystemStateRecord>().InSingle(SystemStateRecord.SingletonId)?.EffectiveRevision != revision)
            { throw new InvalidOperationException("重新核对的意图或有效版本已变化。"); }
            var lastAttempt = intent.LastOperationId is { } operation ? client.Queryable<DeliveryMigrationRecord>().InSingle(operation) : null;
            if (RetryStateProblem(intent, lastAttempt) is not null)
            { throw new InvalidOperationException("上次尝试不能安全重新执行。"); }
            var source = OutputRecipientIdentity.Read(intent.SourceSummary);
            var target = OutputRecipientIdentity.Read(command.Summary);
            if (source?.Protocol != intent.Protocol || target?.Protocol != intent.Protocol || !target.AccountRecorded)
            { throw new InvalidOperationException("不能重新确认未知原协议或未知新身份。"); }
            intent.TargetFingerprint = command.Fingerprint;
            intent.TargetSummary = command.Summary;
            intent.TargetRecipient = target.Address;
            intent.Status = "waiting";
            intent.LastOperationId = command.OperationId.ToString("D");
            client.Updateable(intent).UpdateColumns(row => new { row.TargetFingerprint, row.TargetSummary, row.TargetRecipient, row.Status, row.LastOperationId }).ExecuteCommand();
            return;
        }
        if (intent.LastOperationId is { } previous)
        {
            var attempt = client.Queryable<DeliveryMigrationRecord>().InSingle(previous);
            // 只有已核对为未提交的中断才自动继续。拒绝须管理员重新核对，未知结果绝不重放。
            if (attempt is not { Status: "interrupted" } || attempt.IntentId != intent.IntentId)
            { throw new InvalidOperationException("上次意图尝试尚未核对为未执行，不得重放。"); }
        }
        intent.LastOperationId = command.OperationId.ToString("D");
        client.Updateable(intent).UpdateColumns(row => row.LastOperationId).ExecuteCommand();
    }

    private static void FinishIntent(SqlSugarClient client, DeliveryMigrationRecord attempt)
    {
        if (attempt.IntentId is not { } id)
        { return; }
        var intent = client.Queryable<DeliveryMigrationIntentRecord>().InSingle(id);
        if (intent is null || intent.Status != "waiting" || intent.LastOperationId != attempt.OperationId ||
            intent.ConfigStoreId != attempt.ConfigStoreId || intent.ConnectionId != attempt.SourceConnectionId ||
            intent.ConnectionId != attempt.TargetConnectionId)
        { throw new DeliveryStoreException("意图与执行尝试不一致，不能同步结果或重放。"); }
        intent.Status = attempt.Status == "interrupted" ? "waiting" : attempt.Status;
        client.Updateable(intent).UpdateColumns(row => row.Status).ExecuteCommand();
    }
}
