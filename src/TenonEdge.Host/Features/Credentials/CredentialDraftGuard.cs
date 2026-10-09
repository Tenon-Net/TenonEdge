using SqlSugar;
using TenonEdge.Adapters.Mqtt;
using TenonEdge.Host.Features.Configuration;
using TenonEdge.Host.Features.Connections;

namespace TenonEdge.Host.Features.Credentials;

/// <summary>与凭证写事务共享客户端，草稿不能在检查与写入之间被其他请求替换。种子部署的旧入口仍单独保留。</summary>
internal static class CredentialDraftGuard
{
    public static void Check(SqlSugarClient client, long id, ConnectionKind kind, string? recipient, string? expectedDraftVersion,
        bool requireDraft = false)
    {
        var draft = ConnectionDraftStore.Read(client, id);
        if (draft is null)
        {
            if (requireDraft || expectedDraftVersion is not null)
            { throw new CredentialWriteException("credential.connectionNotFound"); }
            // T1/T2 种子凭证在草稿出现以前就可预置；不把旧种子编号自动变成新草稿。
            return;
        }
        if (string.IsNullOrEmpty(expectedDraftVersion))
        { throw new CredentialWriteException("credential.draftVersionRequired"); }
        if (ConfigurationDraft.ReadVersion(client) != expectedDraftVersion)
        { throw new CredentialWriteException("credential.draftConflict"); }
        if (draft.Kind != kind)
        { throw new CredentialWriteException("credential.protocolConflict"); }
        var target = kind switch
        {
            ConnectionKind.Mqtt => MqttCredentialStore.NormalizeRecipient(new MqttConnectionSettings
            { Host = draft.Mqtt!.Host, Port = draft.Mqtt.Port, Tls = draft.Mqtt.Tls }.Recipient),
            ConnectionKind.Http => HttpCredentialStore.NormalizeRecipient(draft.Http!.BaseAddress),
            _ => throw new CredentialWriteException("credential.protocolConflict"),
        };
        if (recipient is not null && recipient != target)
        { throw new CredentialWriteException("credential.recipientMismatch"); }
    }

    public static void RequireUnreferenced(SqlSugarClient client, long id)
    {
        if (ConnectionDraftStore.HasRetainedReference(client, id))
        { throw new CredentialWriteException("credential.connectionReferenced"); }
    }
}
