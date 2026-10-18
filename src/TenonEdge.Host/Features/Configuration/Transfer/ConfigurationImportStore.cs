using System.Text.Json;
using SqlSugar;
using TenonEdge.Host.Features.Audit;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Credentials;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Security;
using TenonEdge.Host.Storage;
using TenonEdge.Host.Storage.Ids;
using TenonEdge.Runtime.Inputs;
using TenonEdge.Runtime.Outputs;
using TenonEdge.Runtime.Transforms;

namespace TenonEdge.Host.Features.Configuration.Transfer;

internal sealed record ConfigurationImportCandidate(ConfigurationCheckpoint Draft, ConfigurationChange[] Changes,
    ConfigurationImportCredentialDecision[] Credentials, ConfigurationCredentialMetadata[] OriginalCredentials,
    long[] NewConnectionIds, ConfigurationImportMode Mode);

/// <summary>内部导入存储核心。预检只捕获非秘密值；确认在短事务内复核草稿和凭证并原子写草稿/索引/编辑日志/审计。
/// 不持有活跃事务，不访问运行时或发送队列，不修改当前凭证。</summary>
internal sealed class ConfigurationImportStore(ConfigDatabase database, SnowflakeIdGenerator ids, AuditTrail audit, TimeProvider time, TransformCatalog transforms, InputModuleCatalog inputModules, OutputModuleCatalog outputModules)
{
    public ConfigurationImportCandidate Prepare(ReadOnlyMemory<byte> file, ConfigurationImportMode mode, string expectedVersion,
        CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(expectedVersion) || expectedVersion.Length > 64)
        { throw new ArgumentException("草稿版本无效。"); }
        var document = ConfigurationImportDocument.Read(file, token);
        var captured = database.InTransaction(client =>
        {
            var before = ConfigurationCheckpoint.Read(client, expectedVersion);
            return (Before: before, Credentials: ConfigurationCredentialMetadata.Read(client), Reserved: ReservedIdentities(client));
        });
        token.ThrowIfCancellationRequested();
        var plan = ConfigurationImportPlanner.Prepare(document, captured.Before, mode, ids.NextId, token, transforms, inputModules, outputModules);
        var previousIds = captured.Before.Connections.Select(value => value.Id).ToHashSet();
        var added = plan.Draft.Connections.Where(value => !previousIds.Contains(value.Id)).Select(value => value.Id).ToArray();
        CheckReserved(plan.Draft, plan.Changes, captured.Reserved);
        var required = (plan.Draft.RequiredCredentials ?? []).ToDictionary(value => value.ConnectionId);
        var decisions = ConfigurationImportCredentials.Compare(document, captured.Before, plan.Draft, captured.Credentials, required.Keys.ToHashSet());
        var hints = document.Connections.ToDictionary(value => value.ConnectionKey, value => value.Credential?.AccountIdentity, StringComparer.Ordinal);
        var identities = captured.Credentials.ToDictionary(value => value.ConnectionId, value => value.AccountIdentity);
        foreach (var decision in decisions.Where(value => value.Action == "requiresInput"))
        {
            var identity = hints[decision.ConnectionKey] ?? required.GetValueOrDefault(decision.ConnectionId)?.AccountIdentity
                ?? identities.GetValueOrDefault(decision.ConnectionId)
                ?? throw new InvalidOperationException("凭证重填决定缺少账户摘要。");
            required[decision.ConnectionId] = new(decision.ConnectionId, identity, decision.Reason);
        }
        var draft = plan.Draft with { RequiredCredentials = required.Values.OrderBy(value => value.ConnectionId).ToArray() };
        var changes = ConfigurationDiff.Between(captured.Before, draft);
        // Includes the combined local draft, not only the uploaded bytes. Reserve
        // bounded edit-journal growth before retaining any confirmation candidate.
        new ConfigurationImportRows(draft).CheckSize(draft.Edits, changes.Length);
        token.ThrowIfCancellationRequested();
        return new(draft, changes, decisions, captured.Credentials, added, mode);
    }

    // Only the owner-bound, expiring, once-consumed preparation service exposes
    // this operation. Authorization is rechecked in the same transaction as writes.
    public DraftVersion Commit(ConfigurationImportCandidate candidate, ManagementActor actor, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var draft = candidate.Draft;
        var changedRoutes = candidate.Changes.Where(value => value.Kind == "route" && value.Change != "removed").Select(value => value.Id).ToHashSet();
        var rows = new ConfigurationImportRows(draft);
        rows.CheckSize(draft.Edits, candidate.Changes.Length);
        return database.InTransaction(client =>
        {
            token.ThrowIfCancellationRequested();
            var now = time.GetUtcNow().ToUnixTimeMilliseconds();
            var account = actor.RequireConfigurator(client, now);
            ConfigurationDraft.CheckVersion(client, draft.Version);
            // Credential rotations intentionally do not advance the draft version.
            // Recheck all nonsecret metadata captured in the same preview transaction.
            if (!SameCredentials(candidate.OriginalCredentials, ConfigurationCredentialMetadata.Read(client)))
            { throw new DraftWriteException("configuration.importCredentialsChanged"); }
            CheckReserved(candidate.Draft, candidate.Changes, ReservedIdentities(client));
            rows.Write(client, changedRoutes, account.Name, now);
            foreach (var change in candidate.Changes)
            {
                ConfigurationEditJournal.Record(client, change.Kind, change.Id, account.Name, time, change.Change == "removed");
            }
            var version = ConfigurationDraft.Advance(client, time);
            // Final persisted-size check includes actual journal rows. Any failure,
            // including audit insertion, rolls back every row and the new version.
            ConfigurationCheckpoint.CheckSize(client);
            audit.Append(client, new AuditEntry("configuration.imported", AuditResults.Success, account, actor.SourceIp, null,
                JsonSerializer.Serialize(new
                {
                    mode = candidate.Mode == ConfigurationImportMode.Merge ? "merge" : "replace",
                    previousVersion = draft.Version,
                    version,
                    added = candidate.Changes.Count(value => value.Change == "added"),
                    updated = candidate.Changes.Count(value => value.Change == "modified"),
                    removed = candidate.Changes.Count(value => value.Change == "removed"),
                    credentialsRequired = draft.RequiredCredentials?.Length ?? 0,
                })));
            return new DraftVersion(version);
        });
    }

    private static bool SameCredentials(ConfigurationCredentialMetadata[] left, ConfigurationCredentialMetadata[] right) =>
        left.OrderBy(value => value.ConnectionId).ThenBy(value => value.Kind).SequenceEqual(right.OrderBy(value => value.ConnectionId).ThenBy(value => value.Kind));

    private static void CheckReserved(ConfigurationCheckpoint draft, ConfigurationChange[] changes, Reserved reserved)
    {
        var devices = draft.Devices.ToDictionary(value => value.Id, value => value.Key);
        foreach (var change in changes.Where(value => value.Change == "added"))
        {
            if (change.Kind == "connection" && reserved.Connections.Contains(change.Id))
            { throw new DraftWriteException("connection.reservedIdentity"); }
            if (change.Kind == "device" && reserved.Devices.Contains(change.Key))
            { throw new DraftWriteException("device.reservedKey"); }
            if (change.Kind == "input" && reserved.Inputs.Contains(devices[change.DeviceId!.Value] + "/" + change.Key))
            { throw new DraftWriteException("input.reservedKey"); }
        }
    }

    private sealed record Reserved(HashSet<long> Connections, HashSet<string> Devices, HashSet<string> Inputs);

    private static Reserved ReservedIdentities(SqlSugarClient client)
    {
        var reserved = new Reserved(client.Queryable<HttpCredentialRecord>().Select(value => value.ConnectionId).ToList()
            .Concat(client.Queryable<MqttCredentialRecord>().Select(value => value.ConnectionId).ToList())
            .Concat(client.Queryable<ConnectionTrustRecord>().Select(value => value.ConnectionId).ToList()).ToHashSet(),
            new(StringComparer.Ordinal), new(StringComparer.Ordinal));
        var state = client.Queryable<SystemStateRecord>().InSingle(SystemStateRecord.SingletonId);
        foreach (var revision in new[] { state.EffectiveRevision, state.PreviousRevision, state.PendingRevision }.OfType<long>().Distinct())
        {
            var snapshot = client.Queryable<RuntimeSnapshotRecord>().InSingle(revision)
                ?? throw new InvalidOperationException("运行版本引用缺失。");
            foreach (var unit in RuntimeSnapshotDocument.ReadExecutionPlan(snapshot).Units)
            {
                foreach (var id in RuntimeConnectionReferences.ReadIds(unit))
                { reserved.Connections.Add(id); }
                if (unit.Id.Kind.StartsWith("input.", StringComparison.Ordinal))
                { AddInput(unit.Id.Key); }
                if (unit.Configuration.TryGetProperty("inputs", out var inputs))
                { foreach (var input in inputs.EnumerateArray()) { AddInput(input.GetString()!); } }
            }
        }
        return reserved;

        void AddInput(string key)
        {
            reserved.Inputs.Add(key);
            reserved.Devices.Add(key.Split('/')[0]);
        }
    }
}
