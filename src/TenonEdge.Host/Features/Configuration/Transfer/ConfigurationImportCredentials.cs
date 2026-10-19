using TenonEdge.Adapters.Mqtt;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Credentials;
using TenonEdge.Runtime.Connections;

namespace TenonEdge.Host.Features.Configuration.Transfer;

internal sealed record ConfigurationImportCredentialDecision(long ConnectionId, string ConnectionKey, string Action, string Reason);

/// <summary>只比较非秘密身份，不更新当前凭证。需要填写的连接由后续草稿阻断记录保护，不能降级成匿名连接。</summary>
internal static class ConfigurationImportCredentials
{
    public static ConfigurationImportCredentialDecision[] Compare(PortableConfiguration file, ConfigurationCheckpoint before,
        ConfigurationCheckpoint after, ConfigurationCredentialMetadata[] current, IReadOnlySet<long> alreadyRequired, ConnectionModuleCatalog? modules = null)
    {
        var old = before.Connections.ToDictionary(value => value.Key, StringComparer.Ordinal);
        var next = after.Connections.ToDictionary(value => value.Key, StringComparer.Ordinal);
        var metadata = new Dictionary<long, ConfigurationCredentialMetadata>();
        foreach (var credential in current)
        {
            if (!metadata.TryAdd(credential.ConnectionId, credential))
            { throw ConfigurationImportDocument.Error("import.credentialConflict", "$.connections"); }
        }
        return file.Connections.Select(value =>
        {
            var connection = next[value.ConnectionKey];
            if (alreadyRequired.Contains(connection.Id))
            { return Decision("requiresInput", "alreadyRequiresInput"); }
            if (!metadata.TryGetValue(connection.Id, out var credential))
            { return value.Credential is null ? Decision("none", "noCredential") : Decision("requiresInput", "exportRequiresInput"); }
            if (credential.Kind != connection.Kind)
            { return Decision("requiresInput", "protocolChanged"); }
            if (!credential.HasSecret)
            { return Decision("requiresInput", "credentialUnavailable"); }
            var recipient = Recipient(connection, modules);
            // The stored credential may still be bound to an older address than
            // the current draft. Comparing the two draft addresses alone is unsafe.
            if (!old.TryGetValue(value.ConnectionKey, out var previous) || Recipient(previous, modules) != recipient || credential.Recipient != recipient)
            { return Decision("requiresInput", "recipientChanged"); }
            if (value.Credential?.AccountIdentity != credential.AccountIdentity)
            { return Decision("requiresInput", "accountChanged"); }
            return Decision("preserved", "sameRecipientAndAccount");

            ConfigurationImportCredentialDecision Decision(string action, string reason) => new(connection.Id, connection.Key, action, reason);
        }).ToArray();
    }

    private static string? ModuleRecipient(ConnectionDraft connection, ConnectionModuleCatalog? modules)
    {
        try
        {
            return connection.Module is { } module ? modules?.Definition(module.Snapshot(connection.Id)).RecipientIdentity(module.Parameters) : null;
        }
        catch (ArgumentException) { return null; }
    }

    internal static string? Recipient(ConnectionDraft connection, ConnectionModuleCatalog? modules = null) => connection.Kind switch
    {
        ConnectionKind.Mqtt => MqttCredentialStore.NormalizeRecipient(new MqttConnectionSettings
        { Host = connection.Mqtt!.Host, Port = connection.Mqtt.Port, Tls = connection.Mqtt.Tls }.Recipient),
        ConnectionKind.Http => HttpCredentialStore.NormalizeRecipient(connection.Http!.BaseAddress),
        ConnectionKind.Module => ModuleRecipient(connection, modules),
        _ => null,
    };
}
