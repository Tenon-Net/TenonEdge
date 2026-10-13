using System.Text;
using System.Text.Json.Serialization;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Host.Features.Deliveries;
using TenonEdge.Host.Features.Routes;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Runtime.Deliveries;

namespace TenonEdge.Host.Features.Configuration;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ConfigurationMigrationPrepareRequest(string ExpectedVersion, long[] ConnectionIds);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ConfigurationMigrationConfirmation(bool Confirmed, bool SameOwnerConfirmed, string? ChangeDescription = null);
internal sealed record ConfigurationMigrationTargetReview(Guid IntentId, long ConnectionId, string Key, string Name,
    OutputRecipientIdentity Source, OutputRecipientIdentity Target, int Count, DeliveryMigrationQuotaDeficit Quota);
internal sealed record ConfigurationMigrationReview(Guid Id, ConfigurationReview Configuration, ConfigurationMigrationTargetReview[] Targets, DateTimeOffset ExpiresAt)
{
    public bool CanConfirm => Targets.Length > 0 && Targets.All(target => target.Count > 0 && !target.Quota.Exceeded);
}

/// <summary>形式 A 应用确认：全网关一份有界清单，属于当前会话，15分钟单调过期。
/// 只构建/读身份与配额，不冻结配置、不暂停或联网；确认一次消费，之后操作归原协调者所有。</summary>
internal sealed class ConfigurationMigrationPreparation(RuntimeVersionStore versions, RouteDraftStore drafts,
    RuntimeCoordinator coordinator, DeliveryCache cache, TimeProvider time)
{
    internal static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);
    private const int MaximumIdentityBytes = 4 * 1024 * 1024;
    private readonly Lock _gate = new();
    private Prepared? _current;
    private bool _preparing;

    public async Task<ConfigurationMigrationReview> PrepareAsync(ConfigurationMigrationPrepareRequest request, DeliveryMigrationActor actor, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (request.ConnectionIds is not { Length: >= 1 and <= 500 } ids || ids.Any(id => id is < 1 or > 9007199254740991L) ||
            ids.Distinct().Count() != ids.Length)
        { throw new ArgumentException("请选择1至500个不同的受阻连接。"); }
        lock (_gate)
        {
            Expire();
            if (_preparing || _current is { } current && current.Owner != actor.Owner)
            { throw Error(409, "configuration.migrationPreparationBusy"); }
            _preparing = true;
        }
        try
        {
            var review = versions.ReviewDraft(request.ExpectedVersion, token);
            if (!review.ComparisonAvailable || review.PlanHash is null || review.RouteIssues.Length != 0 || review.EffectiveRevision is not { } revision ||
                ids.Any(id => !review.TargetBlocks.Any(block => block.ConnectionId == id)))
            { throw Error(409, "configuration.migrationPreflightFailed"); }
            var snapshot = drafts.ReadCompilation(request.ExpectedVersion, token);
            var document = RoutePlanCompiler.Compile(snapshot, token).Document;
            if (document.ContentHash != review.PlanHash || versions.ReadPointers().EffectiveRevision != revision)
            { throw Error(409, "configuration.migrationReviewChanged"); }
            await using var prepared = coordinator.Runtime.Prepare(document.ToPlan(checked(revision + 1)));
            if (prepared.Failures.Count != 0)
            { throw Error(409, "configuration.migrationPreflightFailed"); }
            var intents = new List<DeliveryMigrationIntentDraft>(ids.Length);
            var targets = new List<ConfigurationMigrationTargetReview>(ids.Length);
            var identityBytes = 0;
            foreach (var id in ids.Order())
            {
                token.ThrowIfCancellationRequested();
                var source = coordinator.Runtime.CaptureMigrationTarget(id, revision);
                var target = prepared.CaptureMigrationTarget(id);
                if (source is null || target is null || !target.Recipient.AccountRecorded)
                { throw Error(409, "configuration.migrationTargetUnavailable"); }
                if (source.Recipient.Protocol != target.Recipient.Protocol || source.Recipient.Address == target.Recipient.Address)
                { throw Error(409, "configuration.migrationPreflightFailed"); }
                identityBytes = checked(identityBytes + Encoding.UTF8.GetByteCount(source.Summary) + Encoding.UTF8.GetByteCount(target.Summary));
                if (identityBytes > MaximumIdentityBytes)
                { throw Error(409, "configuration.migrationIdentityLimit"); }
                // 这里只构造候选声明，直到 Confirm 检查同所有者声明并消费清单才交协调者；不是客户端传入资料。
                var intent = new DeliveryMigrationIntentDraft(new(cache.Options.ConfigStoreId, Guid.NewGuid(), id, source.Fingerprint),
                    source.Recipient, target.Recipient.Address, true, target);
                intents.Add(intent);
                var quota = cache.PreflightMigration(DeliveryMigrationCommand.OldTarget(cache.Options.ConfigStoreId, intent.Hold.IntentId,
                    id, source.Fingerprint, target.Fingerprint, target.Summary));
                if (quota.Error is not null && quota.Error != DeliveryMigrationErrors.QuotaExceeded)
                { throw Error(409, quota.Error); }
                var connection = snapshot.Connections[id];
                targets.Add(new(intent.Hold.IntentId, id, connection.Key, connection.Name, source.Recipient, target.Recipient, quota.Count, quota.Quota!));
            }
            try
            {
                // 所有未选阻断、删目标、改协议、夹带 Topic/路径变更仍由同一版本存储规则拒绝。
                if (versions.TargetChangeProblem(document, intents) is not null)
                { throw Error(409, "configuration.migrationPreflightFailed"); }
            }
            catch (InvalidOperationException error) when (error is not DeliveryMigrationRequestException)
            { throw Error(409, "configuration.migrationPreflightFailed"); }
            if (versions.ReadPointers().EffectiveRevision != revision)
            { throw Error(409, "configuration.migrationReviewChanged"); }
            token.ThrowIfCancellationRequested();
            var result = new ConfigurationMigrationReview(Guid.NewGuid(), review, targets.ToArray(), time.GetUtcNow() + Lifetime);
            lock (_gate)
            { _current = new(actor.Owner, time.GetTimestamp(), result, intents.ToArray()); }
            return result;
        }
        finally { lock (_gate) { _preparing = false; } }
    }

    public ConfigurationMigrationReview? Current(string owner)
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
        {
            _ = Require(id, owner);
            _current = null;
        }
    }

    public RuntimeApplicationStatus Confirm(Guid id, ConfigurationMigrationConfirmation request, DeliveryMigrationActor actor, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!request.Confirmed || !request.SameOwnerConfirmed)
        { throw new ArgumentException("须明确确认变更清单，并声明每个所选连接的新旧接收方属于同一数据所有者。"); }
        var description = ConfigurationChangeDescription.Normalize(request.ChangeDescription);
        Prepared selected;
        lock (_gate)
        {
            selected = Require(id, actor.Owner);
            if (!selected.Review.CanConfirm)
            { throw Error(409, "configuration.migrationNotConfirmable"); }
            _current = null;
        }
        var expected = selected.Review.Configuration;
        var current = versions.ReviewDraft(expected.Version, token);
        if (!current.ComparisonAvailable || current.RouteIssues.Length != 0 || current.PlanHash != expected.PlanHash || current.EffectiveRevision != expected.EffectiveRevision)
        { throw Error(409, "configuration.migrationReviewChanged"); }
        foreach (var intent in selected.Intents)
        {
            var source = coordinator.Runtime.CaptureMigrationTarget(intent.Hold.ConnectionId, expected.EffectiveRevision!.Value);
            if (source?.Fingerprint != intent.Hold.OldFingerprint || source.Recipient != intent.Source)
            { throw Error(409, "configuration.migrationReviewChanged"); }
        }
        return coordinator.BeginApplyWithMigrations(id, expected.Version, new(expected.EffectiveRevision, expected.PlanHash!),
            selected.Intents, actor.Account, actor.SourceIp, description, token);
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
    private sealed record Prepared(string Owner, long Timestamp, ConfigurationMigrationReview Review, DeliveryMigrationIntentDraft[] Intents);
}
