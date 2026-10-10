using SqlSugar;
using TenonEdge.Adapters.Mqtt;
using TenonEdge.Host.Features.Configuration;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Credentials;
using TenonEdge.Host.Storage;

namespace TenonEdge.Host.Features.Runtime;

internal sealed record RuntimeRollbackPreparation(long Revision, RuntimePlanDocument Document);

internal sealed partial class RuntimeVersionStore
{
    public RuntimeRollbackPreparation FreezeManualRollback(long targetRevision, string expectedDraftVersion, string account, string? changeDescription = null, RuntimeApplicationExpectation? expectation = null)
    {
        var captured = ReadManualRollback(targetRevision, expectedDraftVersion);
        var draft = RequiredDraft(captured.Target);
        var document = RuntimeSnapshotDocument.ReadPlan(captured.Target);
        if (expectation is not null && document.ContentHash != expectation.PlanHash)
        { throw new DraftWriteException("configuration.reviewChanged"); }
        var revision = FreezeCore(new(captured.Target.SchemaVersion, captured.Target.Content, captured.Target.ContentHash),
            RuntimeSnapshotOrigins.Rollback, $"r{targetRevision}", account, expectedDraftVersion, client =>
            {
                if (State(client).EffectiveRevision != captured.Effective.Revision ||
                    RequiredSnapshot(client, targetRevision).ContentHash != captured.Target.ContentHash)
                { throw new RuntimeVersionConflictException("runtime.versionChanged", targetRevision); }
                ValidateRollbackReferences(client, draft);
            }, changeDescription, draft.Edits, expectation);
        return new(revision, document);
    }

    // 预检和冻结使用相同的草稿干净/目标状态前提，不分配版本或改变指针。
    private (RuntimeSnapshotRecord Effective, RuntimeSnapshotRecord Target, ConfigurationCheckpoint Draft) ReadManualRollback(long targetRevision, string expectedDraftVersion)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetRevision);
        if (string.IsNullOrWhiteSpace(expectedDraftVersion) || expectedDraftVersion.Length > 64)
        { throw new ArgumentException("须提供有效草稿版本。", nameof(expectedDraftVersion)); }
        var captured = database.InTransaction(client =>
        {
            ConfigurationDraft.CheckVersion(client, expectedDraftVersion);
            var state = State(client);
            var effective = state.EffectiveRevision is { } revision ? RequiredSnapshot(client, revision)
                : throw new DraftWriteException("configuration.noEffectiveVersion");
            var target = RequiredSnapshot(client, targetRevision);
            if (target.State is not (RuntimeSnapshotStates.Effective or RuntimeSnapshotStates.History))
            { throw new DraftWriteException("configuration.rollbackTargetNotEffective"); }
            return (Effective: effective, Target: target, Draft: ConfigurationCheckpoint.Read(client, expectedDraftVersion));
        });
        if (ConfigurationDiff.Between(RequiredDraft(captured.Effective), captured.Draft).Length != 0)
        { throw new DraftWriteException("configuration.unappliedChanges"); }
        return captured;
    }

    private void ValidateRollbackReferences(SqlSugarClient client, ConfigurationCheckpoint draft)
    {
        if (draft.CredentialConnections is null && draft.Connections.Any(connection => connection.Kind is ConnectionKind.Mqtt or ConnectionKind.Http))
        { throw new NotSupportedException("该旧检查点没有凭证引用记录，不能确认安全回滚；请通过草稿重新配置并应用。"); }
        var requiredCredentials = draft.CredentialConnections?.ToHashSet() ?? [];
        foreach (var connection in draft.Connections)
        {
            var current = ConnectionDraftStore.Read(client, connection.Id);
            if (current is null || current.Kind != connection.Kind || current.Key != connection.Key)
            { throw new DraftWriteException("configuration.rollbackConnectionMissing"); }
            if (!requiredCredentials.Contains(connection.Id))
            { continue; }
            var available = connection.Kind switch
            {
                ConnectionKind.Http => client.Queryable<HttpCredentialRecord>().InSingle(connection.Id) is { ProtectedSecret: not null } http &&
                    http.Recipient == connection.Http!.BaseAddress && httpCredentials.ResolveHttp(connection.Id, http.Recipient) is not null,
                ConnectionKind.Mqtt => client.Queryable<MqttCredentialRecord>().InSingle(connection.Id) is { ProtectedPassword: not null } mqtt &&
                    mqtt.Recipient == new MqttConnectionSettings
                    {
                        ConnectionId = connection.Id,
                        Host = connection.Mqtt!.Host,
                        Port = connection.Mqtt.Port,
                        Tls = connection.Mqtt.Tls
                    }.Recipient && credentials.ResolveMqtt(connection.Id, mqtt.Recipient) is not null,
                _ => false,
            };
            if (!available)
            { throw new DraftWriteException("configuration.rollbackCredentialMissing"); }
        }
    }
}
