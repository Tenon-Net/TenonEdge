using TenonEdge.Host.Features.Audit;
using TenonEdge.Runtime.Deliveries;

namespace TenonEdge.Host.Features.Deliveries;

internal sealed partial class DeliveryMigrationStore
{
    // 只允许明确拒绝，或旧版缺少确认目标且从未开始的等待意图。started绝不能被GET/重试标成中断。
    private static string? RetryStateProblem(DeliveryMigrationIntentRecord intent, DeliveryMigrationRecord? attempt) =>
        intent.Status == "completed" ? "configuration.migrationAlreadyCompleted"
        : intent.Status == "rejected" && attempt is { Status: "rejected" } && attempt.IntentId == intent.IntentId ? null
        : intent.Status == "waiting" && intent.LastOperationId is null && attempt is null &&
            (intent.TargetFingerprint is null || intent.TargetSummary is null) ? null
        : "configuration.migrationNotRetryable";

    public string? IntentRetryProblem(DeliveryMigrationIntentRecord intent)
    {
        if (intent.ConfigStoreId != identity.ConfigStoreId)
        { return DeliveryMigrationErrors.StoreChanged; }
        var attempt = intent.LastOperationId is { } id ? Read(Guid.Parse(id)) : null;
        var problem = RetryStateProblem(intent, attempt);
        if (problem is not null)
        { return problem; }
        if (attempt is not null && (attempt.ConfigStoreId != intent.ConfigStoreId ||
            attempt.SourceConnectionId != intent.ConnectionId || attempt.TargetConnectionId != intent.ConnectionId ||
            queue.ReadMigrationReceipt(Guid.Parse(attempt.OperationId)) is not null))
        { return "configuration.migrationOutcomeUnreconciled"; }
        using var client = database.CreateIsolatedClient();
        return client.Queryable<DeliveryMigrationIntentRecord>().Any(row => row.ConfigStoreId == intent.ConfigStoreId &&
            row.ConnectionId == intent.ConnectionId && row.Status == "waiting" && row.Revision < intent.Revision)
            ? "configuration.migrationEarlierIntentWaiting" : null;
    }

    // 由原协调者持有唯一队列操作准入时调用；审批、开始、指针和审计同一事务，不先置waiting再让轮询抢跑。
    public void BeginIntentRetry(DeliveryMigrationCommand command, long revision, DeliveryMigrationIntentRecord expected,
        AuditAccount account, string? sourceIp)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(expected);
        if (command.Hold?.RequestHash != expected.ToHold().RequestHash || IntentRetryProblem(expected) is not null ||
            queue.ReadMigrationHold(Guid.Parse(expected.IntentId))?.RequestHash != expected.ToHold().RequestHash)
        { throw new InvalidOperationException("意图尚不能按原隔离范围重新执行。"); }
        BeginCore(command, revision, expected.SourceSummary, true, account, sourceIp, expected, retry: true);
    }

    internal static bool MatchesRetryReview(DeliveryMigrationIntentRecord current, DeliveryMigrationIntentRecord expected) =>
        current.IntentId == expected.IntentId && current.Revision == expected.Revision && current.SourceRevision == expected.SourceRevision &&
        current.ToHold().RequestHash == expected.ToHold().RequestHash && current.Protocol == expected.Protocol &&
        current.Status == expected.Status && current.LastOperationId == expected.LastOperationId &&
        current.SourceSummary == expected.SourceSummary && current.TargetRecipient == expected.TargetRecipient &&
        current.TargetFingerprint == expected.TargetFingerprint && current.TargetSummary == expected.TargetSummary &&
        current.AccountName == expected.AccountName && current.TokenName == expected.TokenName && current.SourceIp == expected.SourceIp;
}
