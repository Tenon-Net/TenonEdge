using System.Text.Json;
using SqlSugar;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Adapters.Http;
using TenonEdge.Adapters.Mqtt;
using TenonEdge.Host.Features.Configuration;
using TenonEdge.Host.Features.Credentials;
using TenonEdge.Host.Security;
using TenonEdge.Host.Storage;
using TenonEdge.Runtime.Deliveries;

namespace TenonEdge.Host.Features.Runtime;

internal sealed record RuntimeRecoveryCheckpoint(long EffectiveRevision, string DamageStamp, RuntimeSnapshotRecord Target,
    RuntimePlanDocument Plan, ConfigurationCheckpoint Draft, string CredentialStamp);
internal sealed record RuntimeRecoveryTarget(long ConnectionId, OutputTargetIdentity Identity);
internal sealed record RuntimeRecoveryAuthorization(RuntimeRecoveryCheckpoint Checkpoint, RuntimeRecoveryTarget[] Targets, ManagementActor Actor);
internal sealed record ConfigurationRecoveryReview(long EffectiveRevision, long TargetRevision, string DraftVersion,
    string ConfirmationHash, string TargetContentHash, RuntimeRecoveryTarget[] Targets, DeliveryRecoveryCheck Queue, string[] PreparationProblems)
{
    public bool PreservesDraft { get; } = true;
    public bool CanConfirm => Queue.BlockedCount == 0 && PreparationProblems.Length == 0;
}

internal sealed partial class RuntimeVersionStore
{
    public string ReadRecoveryDraftVersion()
    {
        using var client = database.CreateIsolatedClient();
        return ConfigurationDraft.ReadVersion(client);
    }

    public RuntimeRecoveryCheckpoint ReadRecovery(long targetRevision, string expectedDraftVersion) => database.InTransaction(client =>
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetRevision);
        ConfigurationDraft.CheckVersion(client, expectedDraftVersion);
        var state = State(client);
        if (state.PendingRevision is not null || state.EffectiveRevision is not { } effective || effective == targetRevision)
        { throw new DraftWriteException("configuration.recoveryUnavailable"); }
        var damage = DamageStamp(client.Queryable<RuntimeSnapshotRecord>().InSingle(effective));
        var target = RequiredSnapshot(client, targetRevision);
        if (target.State != RuntimeSnapshotStates.History)
        { throw new DraftWriteException("configuration.rollbackTargetNotEffective"); }
        var parsed = RuntimeSnapshotDocument.ReadForExecution(target);
        var draft = parsed.Draft ?? throw new NotSupportedException("恢复目标须包含完整非秘密草稿。");
        ValidateRecoveryReferences(client, draft);
        return new RuntimeRecoveryCheckpoint(effective, damage, target, parsed.Plan,
            ConfigurationCheckpoint.Read(client, expectedDraftVersion), CredentialStamp(client, draft));
    });

    // A recovery proof binds the actual damaged bytes, not the untrusted stored hash.
    // A repaired/changed effective version invalidates approval, including a missing row appearing.
    private static string DamageStamp(RuntimeSnapshotRecord? snapshot)
    {
        if (snapshot is null)
        { return "missing"; }
        try
        { _ = RuntimeSnapshotDocument.Read(snapshot); }
        catch (RuntimePlanDocumentException)
        {
            return RuntimePlanDocument.HashOf(JsonSerializer.Serialize(new
            { snapshot.SchemaVersion, snapshot.ContentHash, ActualHash = RuntimePlanDocument.HashOf(snapshot.Content) }));
        }
        throw new DraftWriteException("configuration.recoveryNotCorrupt");
    }

    private static string CredentialStamp(SqlSugarClient client, ConfigurationCheckpoint target)
    {
        var ids = target.Connections.Select(connection => connection.Id).ToHashSet();
        var mqtt = client.Queryable<MqttCredentialRecord>().Select(row => new { row.ConnectionId, row.Version }).ToList()
            .Where(row => ids.Contains(row.ConnectionId)).OrderBy(row => row.ConnectionId).ToArray();
        var http = client.Queryable<HttpCredentialRecord>().Select(row => new { row.ConnectionId, row.Version }).ToList()
            .Where(row => ids.Contains(row.ConnectionId)).OrderBy(row => row.ConnectionId).ToArray();
        return RuntimePlanDocument.HashOf(JsonSerializer.Serialize(new { mqtt, http }));
    }

    public void ValidateRecovery(RuntimeRecoveryAuthorization recovery, bool requireDraftVersion) => database.InTransaction(client =>
    { ValidateRecovery(client, recovery, requireDraftVersion); return true; });

    private void ValidateRecovery(SqlSugarClient client, RuntimeRecoveryAuthorization recovery, bool requireDraftVersion)
    {
        _ = recovery.Actor.RequireAdministrator(client, time.GetUtcNow().ToUnixTimeMilliseconds());
        var captured = recovery.Checkpoint;
        if (requireDraftVersion)
        { ConfigurationDraft.CheckVersion(client, captured.Draft.Version); }
        var state = State(client);
        var target = RequiredSnapshot(client, captured.Target.Revision);
        if (state.EffectiveRevision != captured.EffectiveRevision || target.State != RuntimeSnapshotStates.History ||
            target.ContentHash != captured.Target.ContentHash || target.Content != captured.Target.Content || target.SchemaVersion != captured.Target.SchemaVersion ||
            DamageStamp(client.Queryable<RuntimeSnapshotRecord>().InSingle(captured.EffectiveRevision)) != captured.DamageStamp)
        { throw new DraftWriteException("configuration.reviewChanged"); }
        var draft = RequiredDraft(target);
        ValidateRecoveryReferences(client, draft);
        if (CredentialStamp(client, draft) != captured.CredentialStamp)
        { throw new DraftWriteException("configuration.reviewChanged"); }
    }

    private void ValidateRecoveryReferences(SqlSugarClient client, ConfigurationCheckpoint draft)
    {
        ValidateRollbackReferences(client, draft);
        // HTTP always resolves the current credential, even when the old snapshot was
        // anonymous. Do not approve an identity whose secret is missing or bound elsewhere.
        foreach (var connection in draft.Connections.Where(connection => connection.Http is not null))
        {
            if (client.Queryable<HttpCredentialRecord>().InSingle(connection.Id) is { } current &&
                (current.Recipient != connection.Http!.BaseAddress || current.ProtectedSecret is null ||
                    httpCredentials.ResolveHttp(connection.Id, current.Recipient) is null))
            { throw new DraftWriteException("configuration.rollbackCredentialMissing"); }
        }
    }

    public RuntimeRollbackPreparation FreezeRecovery(RuntimeRecoveryAuthorization recovery, string account, string? description)
    {
        var captured = recovery.Checkpoint;
        var target = captured.Target;
        var revision = FreezeCore(new(target.SchemaVersion, target.Content, target.ContentHash), RuntimeSnapshotOrigins.Recovery,
            $"r{target.Revision}; damaged r{captured.EffectiveRevision}", account, captured.Draft.Version,
            client => ValidateRecovery(client, recovery, requireDraftVersion: true), description, RequiredDraft(target).Edits,
            new(captured.EffectiveRevision, captured.Plan.ContentHash));
        return new(revision, captured.Plan);
    }

    public DeliveryRecoveryCheck CheckRecoveryQueue(RuntimeRecoveryAuthorization recovery, CancellationToken token)
    {
        var rules = new Dictionary<long, List<Func<string, string?, bool>>>();
        var queueOnly = new HashSet<long>();
        foreach (var unit in recovery.Checkpoint.Plan.Units.Select(RuntimeConnectionReferences.Protocol))
        {
            long id;
            Func<string, string?, bool> accepts;
            if (unit.Id.Kind == "output.mqtt")
            {
                var settings = MqttOutputSettings.Parse(unit.Configuration);
                id = settings.ConnectionId;
                if (settings.QueueOnly)
                { queueOnly.Add(id); continue; }
                accepts = (device, address) => address == settings.TopicFor(device);
            }
            else if (unit.Id.Kind == "output.http")
            {
                var settings = HttpOutputSettings.Parse(unit.Configuration);
                id = settings.ConnectionId;
                if (settings.QueueOnly)
                { queueOnly.Add(id); continue; }
                accepts = (_, address) => address == settings.Path;
            }
            else
            { continue; }
            if (!rules.TryGetValue(id, out var list))
            { rules[id] = list = []; }
            list.Add(accepts);
        }
        var identities = recovery.Targets.ToDictionary(target => target.ConnectionId, target => target.Identity.Fingerprint);
        return deliveries.CheckRecoveryTargets((id, fingerprint, device, address) =>
            identities.TryGetValue(id, out var expected) && fingerprint == expected &&
            (rules.TryGetValue(id, out var checks) ? checks.Any(check => check(device, address)) : queueOnly.Contains(id)), token);
    }

    public static string RecoveryConfirmation(RuntimeRecoveryAuthorization recovery) => RuntimePlanDocument.HashOf(JsonSerializer.Serialize(new
    {
        recovery.Checkpoint.EffectiveRevision,
        recovery.Checkpoint.DamageStamp,
        TargetRevision = recovery.Checkpoint.Target.Revision,
        recovery.Checkpoint.Target.ContentHash,
        recovery.Checkpoint.Draft.Version,
        recovery.Checkpoint.CredentialStamp,
        recovery.Targets,
    }));
}
