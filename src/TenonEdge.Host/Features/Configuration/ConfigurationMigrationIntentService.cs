using System.Text.Json.Serialization;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Host.Features.Deliveries;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Runtime.Deliveries;

namespace TenonEdge.Host.Features.Configuration;

internal sealed record ConfigurationMigrationIntentInspection(Guid IntentId, string Status, long? EffectiveRevision,
    OutputRecipientIdentity? Source, OutputRecipientIdentity? ApprovedTarget, OutputRecipientIdentity? CurrentTarget,
    bool TargetMatchesConfirmation, int? RemainingRecords, DeliveryMigrationQuotaDeficit? CurrentQuota, string? Problem, string? RetryProblem)
{
    public bool CanPrepareRetry => Problem is null && RetryProblem is null && RemainingRecords is >= 0 && CurrentQuota is { Exceeded: false };
}
internal sealed record ConfigurationMigrationIntentRetryReview(Guid Id, ConfigurationMigrationIntentInspection Inspection, DateTimeOffset ExpiresAt)
{
    public bool CanConfirm => Inspection.CanPrepareRetry;
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ConfigurationMigrationIntentConfirmation(bool Confirmed, bool SameOwnerConfirmed);

/// <summary>只读检查不核对未知结果、不改意图；显式准备按登录会话保存一份有界清单。
/// 确认消费后由原协调者持有执行，审批/开始原子提交，不能先把rejected改waiting让自动轮询抢跑。</summary>
internal sealed class ConfigurationMigrationIntentService(DeliveryMigrationStore store, RuntimeVersionStore versions,
    RuntimeCoordinator coordinator, DeliveryCache cache, TimeProvider time)
{
    internal static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);
    private readonly Lock _gate = new();
    private Prepared? _current;
    private bool _preparing;

    public ConfigurationMigrationIntentInspection Inspect(Guid id, CancellationToken token) => Capture(id, token).Inspection;

    private CaptureResult Capture(Guid id, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var row = store.ReadIntent(id) ?? throw Error(404, "configuration.migrationIntentNotFound");
        var revision = versions.ReadPointers().EffectiveRevision;
        var source = OutputRecipientIdentity.Read(row.SourceSummary);
        var approved = OutputRecipientIdentity.Read(row.TargetSummary);
        var target = revision is { } active ? coordinator.Runtime.CaptureMigrationTarget(row.ConnectionId, active) : null;
        var problem = row.ConfigStoreId != cache.Options.ConfigStoreId ? DeliveryMigrationErrors.StoreChanged
            : source?.Protocol != row.Protocol ? "delivery.migrationOriginalIdentityUnknown"
            : revision is null || target is null || coordinator.Status.State != RuntimeCoordinatorState.Active ? DeliveryMigrationErrors.Unavailable
            : target.Recipient.Protocol != row.Protocol ? DeliveryMigrationErrors.ProtocolMismatch
            : !target.Recipient.AccountRecorded ? "configuration.migrationTargetUnavailable" : null;
        DeliveryMigrationPreflight? check = null;
        if (problem is null)
        {
            check = cache.PreflightMigration(DeliveryMigrationCommand.ForIntent(row.ToHold(), Guid.NewGuid(), target!.Fingerprint, target.Summary));
            problem = check.Error == DeliveryMigrationErrors.QuotaExceeded ? null : check.Error;
        }
        if (revision != versions.ReadPointers().EffectiveRevision || revision is { } current &&
            target != coordinator.Runtime.CaptureMigrationTarget(row.ConnectionId, current))
        { problem = "configuration.migrationReviewChanged"; }
        token.ThrowIfCancellationRequested();
        return new(row, target, new(id, row.Status, revision, source, approved, target?.Recipient,
            target is not null && target.Fingerprint == row.TargetFingerprint && target.Summary == row.TargetSummary,
            check?.Count, check?.Quota, problem, store.IntentRetryProblem(row)));
    }

    public ConfigurationMigrationIntentRetryReview Prepare(Guid id, DeliveryMigrationActor actor, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (_gate)
        {
            Expire();
            if (_preparing || _current is { } current && current.Owner != actor.Owner)
            { throw Error(409, "configuration.migrationPreparationBusy"); }
            _preparing = true;
        }
        try
        {
            var capture = Capture(id, token);
            if (capture.Inspection.Problem is not null || capture.Inspection.RetryProblem is not null)
            { throw Error(409, capture.Inspection.Problem ?? capture.Inspection.RetryProblem!); }
            var review = new ConfigurationMigrationIntentRetryReview(Guid.NewGuid(), capture.Inspection, time.GetUtcNow() + Lifetime);
            lock (_gate)
            { _current = new(actor.Owner, time.GetTimestamp(), capture, review); }
            return review;
        }
        finally { lock (_gate) { _preparing = false; } }
    }

    public ConfigurationMigrationIntentRetryReview? Current(string owner)
    {
        lock (_gate)
        {
            Expire();
            return _current is { } current && current.Owner == owner ? current.Review : null;
        }
    }
    public void Cancel(Guid id, string owner)
    {
        lock (_gate)
        { _ = Require(id, owner); _current = null; }
    }
    public Task<DeliveryWriteResult> Confirm(Guid id, ConfigurationMigrationIntentConfirmation request, DeliveryMigrationActor actor, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!request.Confirmed || !request.SameOwnerConfirmed)
        { throw new ArgumentException("须核对原始及当前接收方并重新声明同一数据所有者。"); }
        Prepared prepared;
        lock (_gate)
        {
            prepared = Require(id, actor.Owner);
            if (!prepared.Review.CanConfirm)
            { throw Error(409, "configuration.migrationNotConfirmable"); }
            _current = null;
        }
        var current = Capture(prepared.Capture.Inspection.IntentId, token);
        if (!DeliveryMigrationStore.MatchesRetryReview(current.Intent, prepared.Capture.Intent) ||
            current.Target != prepared.Capture.Target || current.Inspection.EffectiveRevision != prepared.Capture.Inspection.EffectiveRevision ||
            current.Inspection.Problem is not null || current.Inspection.RetryProblem is not null)
        { throw Error(409, "configuration.migrationReviewChanged"); }
        // 当前配额可能在准备后变化；实际执行仍在原发送者静默之后以唯一写者事务检查并记录拒绝/缺口。
        var command = DeliveryMigrationCommand.ForIntent(current.Intent.ToHold(), id, current.Target!.Fingerprint, current.Target.Summary);
        return coordinator.RetryMigrationIntentAsync(command, current.Inspection.EffectiveRevision!.Value, prepared.Capture.Intent,
            actor.Account, actor.SourceIp, token);
    }
    private Prepared Require(Guid id, string owner)
    {
        Expire();
        if (_current is not { } current || current.Owner != owner || current.Review.Id != id)
        { throw Error(404, "configuration.migrationPreparationNotFound"); }
        if (_preparing)
        { throw Error(409, "configuration.migrationPreparationBusy"); }
        return current;
    }
    private void Expire()
    {
        if (_current is { } current && time.GetElapsedTime(current.Timestamp) >= Lifetime)
        { _current = null; }
    }
    private static DeliveryMigrationRequestException Error(int status, string code) => new(status, code);
    private sealed record CaptureResult(DeliveryMigrationIntentRecord Intent, OutputTargetIdentity? Target, ConfigurationMigrationIntentInspection Inspection);
    private sealed record Prepared(string Owner, long Timestamp, CaptureResult Capture, ConfigurationMigrationIntentRetryReview Review);
}
