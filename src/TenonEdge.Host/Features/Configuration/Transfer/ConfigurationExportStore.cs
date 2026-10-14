using System.Security.Cryptography;
using System.Text.Json;
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
            // Deliberate SQL projection: encrypted secrets, key ring, credential
            // versions and trusted certificates are never materialized for export.
            var credentials = client.Ado.SqlQuery<ExportCredentialRow>("""
                SELECT c.connection_id AS ConnectionId, 'mqtt' AS Protocol, c.username AS Username,
                       NULL AS Kind, NULL AS Name, c.account_marker AS AccountMarker
                FROM mqtt_credential c INNER JOIN connection_draft d ON d.id = c.connection_id
                UNION ALL
                SELECT c.connection_id AS ConnectionId, 'http' AS Protocol, c.username AS Username,
                       c.kind AS Kind, c.name AS Name, c.account_marker AS AccountMarker
                FROM http_credential c INNER JOIN connection_draft d ON d.id = c.connection_id
                """);
            var identities = new Dictionary<long, string>();
            foreach (var credential in credentials)
            {
                var identity = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
                { credential.Protocol, credential.Username, credential.Kind, credential.Name, credential.AccountMarker })));
                if (!identities.TryAdd(credential.ConnectionId, identity))
                { throw new ConfigurationTransferException("configuration.exportCredentialConflict", "$.connections.credential"); }
            }
            return (Draft: draft, Identities: identities);
        });
        token.ThrowIfCancellationRequested();
        return ConfigurationExport.Serialize(ConfigurationExport.Create(captured.Draft, captured.Identities, token), token);
    }

    private sealed class ExportCredentialRow
    {
        public long ConnectionId { get; set; }
        public string Protocol { get; set; } = string.Empty;
        public string? Username { get; set; }
        public int? Kind { get; set; }
        public string? Name { get; set; }
        public string AccountMarker { get; set; } = string.Empty;
    }
}
