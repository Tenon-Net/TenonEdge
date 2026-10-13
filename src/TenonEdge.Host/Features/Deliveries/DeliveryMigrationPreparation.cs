using System.Text.Json;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Runtime.Deliveries;

namespace TenonEdge.Host.Features.Deliveries;

internal sealed record DeliveryMigrationPrepareRequest(long SourceConnectionId, long TargetConnectionId, DeliveryMigrationKey[]? Items);
internal sealed record DeliveryMigrationSourceIdentity(OutputRecipientIdentity Recipient, int Count);
internal sealed record DeliveryMigrationReview(Guid Id, long Revision, long SourceConnectionId, long TargetConnectionId,
    string? TargetKey, string? TargetName, OutputRecipientIdentity Target, DeliveryMigrationSourceIdentity[] Sources,
    int Count, int Pending, int Failed, int Quarantined, DateTimeOffset FirstCreatedAt, DateTimeOffset LastCreatedAt, DateTimeOffset ExpiresAt,
    DeliveryMigrationQuotaDeficit Quota)
{
    public bool CanConfirm => Count > 0 && !Quota.Exceeded;
}
internal sealed record DeliveryMigrationActor(string Owner, string Account, string? SourceIp);
internal sealed class DeliveryMigrationRequestException(int status, string code) : InvalidOperationException(code)
{
    public int StatusCode { get; } = status;
    public string Code { get; } = code;
}

/// <summary>一份有界、归属登录会话的迁移确认清单；不保存请求/事务，不信任客户端自报的协议、身份或指纹。
/// 确认后即消费，实际执行归原协调者所有。请求取消/响应丢失只能查持久结果，不重放确认。</summary>
internal sealed class DeliveryMigrationPreparation(DeliveryCache cache, RuntimeVersionStore versions, RuntimeCoordinator coordinator, TimeProvider time)
{
    internal static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);
    private readonly Lock _gate = new();
    private Prepared? _current;
    private bool _preparing;

    public DeliveryMigrationReview Prepare(DeliveryMigrationPrepareRequest request, DeliveryMigrationActor actor, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (request.SourceConnectionId == request.TargetConnectionId || request.Items is null || request.TargetConnectionId is < 1 or > 9007199254740991L)
        { throw new ArgumentException("请选择另一个有效的同类连接和明确记录。"); }
        lock (_gate)
        {
            Expire();
            if (_preparing || _current is { } current && current.Owner != actor.Owner)
            { throw Error(409, "delivery.migrationPreparationBusy"); }
            _preparing = true;
        }
        try
        {
            var revision = versions.ReadPointers().EffectiveRevision ?? throw Error(409, DeliveryMigrationErrors.Unavailable);
            var target = Target(request.TargetConnectionId, revision);
            var records = cache.ReadMigrationCandidates(request.SourceConnectionId, request.Items);
            var identities = new List<OutputRecipientIdentity>(records.Count);
            foreach (var record in records)
            {
                token.ThrowIfCancellationRequested();
                var identity = OutputRecipientIdentity.Read(record.Summary) ?? throw Error(409, "delivery.migrationOriginalIdentityUnknown");
                if (identity.Protocol != target.Identity.Recipient.Protocol)
                { throw Error(409, DeliveryMigrationErrors.ProtocolMismatch); }
                identities.Add(identity);
            }
            var sources = identities.GroupBy(identity => identity).Select(group => new DeliveryMigrationSourceIdentity(group.Key, group.Count())).ToArray();
            var command = DeliveryMigrationCommand.Selected(cache.Options.ConfigStoreId, Guid.NewGuid(), request.SourceConnectionId,
                request.TargetConnectionId, target.Identity.Fingerprint, target.Identity.Summary, records.Select(record => record.Item).ToArray());
            var preflight = cache.PreflightMigration(command);
            if (preflight.Error is not null && preflight.Error != DeliveryMigrationErrors.QuotaExceeded || preflight.Count != records.Count)
            { throw Error(409, preflight.Error ?? DeliveryMigrationErrors.SelectionChanged); }
            var review = new DeliveryMigrationReview(command.OperationId, revision, request.SourceConnectionId, request.TargetConnectionId,
                target.Key, target.Name, target.Identity.Recipient, sources, records.Count,
                records.Count(record => record.State == DeliveryState.Pending), records.Count(record => record.State == DeliveryState.Failed),
                records.Count(record => record.State == DeliveryState.Quarantined), records.Min(record => record.CreatedAt), records.Max(record => record.CreatedAt),
                time.GetUtcNow() + Lifetime, preflight.Quota!);
            token.ThrowIfCancellationRequested();
            lock (_gate)
            { _current = new(actor.Owner, time.GetTimestamp(), command, JsonSerializer.Serialize(sources, JsonSerializerOptions.Web), review); }
            return review;
        }
        finally { lock (_gate) { _preparing = false; } }
    }

    public DeliveryMigrationReview? Current(string owner)
    {
        lock (_gate)
        {
            Expire();
            return _current is { } value && value.Owner == owner ? value.Review : null;
        }
    }

    public void Cancel(Guid id, string owner)
    {
        lock (_gate)
        {
            Expire();
            if (_current is not { } current || current.Owner != owner || current.Command.OperationId != id)
            { throw Error(404, "delivery.migrationPreparationNotFound"); }
            if (_preparing)
            { throw Error(409, "delivery.migrationPreparationBusy"); }
            _current = null;
        }
    }

    public Task<DeliveryWriteResult> Confirm(Guid id, bool sameOwnerConfirmed, DeliveryMigrationActor actor, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!sameOwnerConfirmed)
        { throw new ArgumentException("须明确确认属于同一数据所有者。"); }
        Prepared prepared;
        lock (_gate)
        {
            Expire();
            if (_current is not { } current || current.Owner != actor.Owner || current.Command.OperationId != id)
            { throw Error(404, "delivery.migrationPreparationNotFound"); }
            if (_preparing || !current.Review.CanConfirm)
            { throw Error(409, "delivery.migrationNotConfirmable"); }
            prepared = current;
            _current = null;
        }
        var identity = Target(prepared.Command.TargetConnectionId, prepared.Review.Revision).Identity;
        if (identity.Fingerprint != prepared.Command.Fingerprint || identity.Summary != prepared.Command.Summary)
        { throw Error(409, DeliveryMigrationErrors.TargetChanged); }
        // 原记录的身份在准备时逐条校验；执行只使用服务器保存的seq/id/指纹/迁移代次。
        // 写者在双发送者静默后再次核对代次和完整配额；已ACK/过期的原记录不参与实际迁移。
        return coordinator.MigrateSelectedDeliveriesAsync(prepared.Command, prepared.Review.Revision, prepared.SourceSummary,
            true, actor.Account, actor.SourceIp, token);
    }

    private (OutputTargetIdentity Identity, string? Key, string? Name) Target(long id, long revision)
    {
        if (versions.ReadPointers().EffectiveRevision != revision)
        { throw Error(409, DeliveryMigrationErrors.TargetChanged); }
        var snapshot = versions.Load(revision) ?? throw new RuntimePlanDocumentException("有效快照缺失。");
        var (plan, draft) = RuntimeSnapshotDocument.ReadForExecution(snapshot);
        var connection = draft?.Connections.SingleOrDefault(item => item.Id == id);
        if (draft is not null && connection is not { Enabled: true } ||
            !plan.Units.Any(unit => unit.Id.Kind is "output.http" or "output.mqtt" && unit.Configuration.GetProperty("connectionId").GetInt64() == id))
        { throw Error(409, DeliveryMigrationErrors.Unavailable); }
        var identity = coordinator.Runtime.CaptureMigrationTarget(id, revision) ?? throw Error(409, DeliveryMigrationErrors.Unavailable);
        if (versions.ReadPointers().EffectiveRevision != revision)
        { throw Error(409, DeliveryMigrationErrors.TargetChanged); }
        return (identity, connection?.Key, connection?.Name);
    }

    private void Expire()
    {
        if (_current is { } current && time.GetElapsedTime(current.PreparedAt) >= Lifetime)
        { _current = null; }
    }
    private static DeliveryMigrationRequestException Error(int status, string code) => new(status, code);
    private sealed record Prepared(string Owner, long PreparedAt, DeliveryMigrationCommand Command, string SourceSummary, DeliveryMigrationReview Review);
}
