using System.Security.Cryptography;
using System.Text.Json;
using SqlSugar;
using TenonEdge.Host.Features.Connections;

namespace TenonEdge.Host.Features.Configuration.Transfer;

/// <summary>非秘密凭证投影；只用于导出标记、导入身份比较及并发核对，不读取或解密密文。</summary>
internal sealed record ConfigurationCredentialMetadata(long ConnectionId, ConnectionKind Kind, string Recipient,
    string Version, string AccountIdentity, bool HasSecret)
{
    public static ConfigurationCredentialMetadata[] Read(SqlSugarClient client) => client.Ado.SqlQuery<CredentialProjection>("""
        SELECT c.connection_id AS ConnectionId, 'mqtt' AS Protocol, c.username AS Username,
               NULL AS Kind, c.account_marker AS AccountMarker, c.recipient AS Recipient,
               c.version AS Version, (c.protected_password IS NOT NULL) AS HasSecret
        FROM mqtt_credential c INNER JOIN connection_draft d ON d.id = c.connection_id
        UNION ALL
        SELECT c.connection_id AS ConnectionId, 'http' AS Protocol,
               CASE WHEN c.kind = 4 THEN c.certificate_subject ELSE c.username END AS Username,
               c.kind AS Kind, c.account_marker AS AccountMarker, c.recipient AS Recipient,
               c.version AS Version, (c.protected_secret IS NOT NULL) AS HasSecret
        FROM http_credential c INNER JOIN connection_draft d ON d.id = c.connection_id
        """).Select(row => new ConfigurationCredentialMetadata(row.ConnectionId,
            row.Protocol == "mqtt" ? ConnectionKind.Mqtt : ConnectionKind.Http, row.Recipient, row.Version,
            AccountDigest(row.Protocol, row.Username, row.Kind, row.AccountMarker), row.HasSecret)).ToArray();

    // Match the existing sender's account-identity semantics. An API-key header/
    // query parameter name and a secret rotation/version are not a new account.
    internal static string AccountDigest(string protocol, string? username, int? kind, string accountMarker) =>
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        { Protocol = protocol, Username = username, Kind = kind, AccountMarker = accountMarker })));

    private sealed class CredentialProjection
    {
        public long ConnectionId { get; set; }
        public string Protocol { get; set; } = string.Empty;
        public string? Username { get; set; }
        public int? Kind { get; set; }
        public string AccountMarker { get; set; } = string.Empty;
        public string Recipient { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public bool HasSecret { get; set; }
    }
}
