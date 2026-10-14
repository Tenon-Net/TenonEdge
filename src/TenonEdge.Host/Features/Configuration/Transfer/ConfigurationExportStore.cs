using TenonEdge.Host.Storage;

namespace TenonEdge.Host.Features.Configuration.Transfer;

/// <summary>同一短事务读取完整草稿及非秘密账户元数据；序列化在事务外，不读取有效版本或发送队列。</summary>
internal sealed class ConfigurationExportStore(ConfigDatabase database)
{
    public byte[] Export(string? expectedVersion, CancellationToken token)
    {
        if (expectedVersion is not null && (string.IsNullOrWhiteSpace(expectedVersion) || expectedVersion.Length > 64))
        { throw new ArgumentException("草稿版本无效。"); }
        token.ThrowIfCancellationRequested();
        var captured = database.InTransaction(client =>
        {
            var version = expectedVersion ?? ConfigurationDraft.ReadVersion(client);
            var draft = ConfigurationCheckpoint.Read(client, version);
            var identities = new Dictionary<long, string>();
            foreach (var credential in ConfigurationCredentialMetadata.Read(client))
            {
                if (!identities.TryAdd(credential.ConnectionId, credential.AccountIdentity))
                { throw new ConfigurationTransferException("configuration.exportCredentialConflict", "$.connections.credential"); }
            }
            return (Draft: draft, Identities: identities);
        });
        token.ThrowIfCancellationRequested();
        return ConfigurationExport.Serialize(ConfigurationExport.Create(captured.Draft, captured.Identities, token), token);
    }

}
