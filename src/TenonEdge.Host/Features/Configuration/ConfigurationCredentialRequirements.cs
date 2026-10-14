using SqlSugar;
using TenonEdge.Host.Features.Connections;

namespace TenonEdge.Host.Features.Configuration;

/// <summary>导入只修改草稿侧的待补凭证标记，不删除/重绑定正在运行的当前凭证。</summary>
internal sealed record ConfigurationCredentialRequirement(long ConnectionId, string AccountIdentity, string Reason);

internal static class ConfigurationCredentialRequirements
{
    public static ConfigurationCredentialRequirement[] Read(SqlSugarClient client) => client.Ado.SqlQuery<CredentialRequirementRecord>("""
        SELECT r.connection_id, r.account_identity, r.reason
        FROM configuration_credential_requirement r INNER JOIN connection_draft d ON d.id = r.connection_id
        ORDER BY r.connection_id
        """).Select(value => new ConfigurationCredentialRequirement(value.ConnectionId, value.AccountIdentity, value.Reason)).ToArray();

    public static void Replace(SqlSugarClient client, ConfigurationCredentialRequirement[]? requirements)
    {
        var rows = (requirements ?? []).Select(value => new CredentialRequirementRecord
        { ConnectionId = value.ConnectionId, AccountIdentity = value.AccountIdentity, Reason = value.Reason }).ToArray();
        if (rows.Select(value => value.ConnectionId).Distinct().Count() != rows.Length ||
            rows.Any(value => value.ConnectionId is < 1 or > 9007199254740991L || value.AccountIdentity is not { Length: 71 } digest ||
                !digest.StartsWith("sha256:", StringComparison.Ordinal) || digest[7..].Any(character => !char.IsAsciiHexDigitLower(character)) ||
                string.IsNullOrEmpty(value.Reason) || value.Reason.Length > 64))
        { throw new ArgumentException("凭证待补标记无效。"); }
        var connections = client.Queryable<ConnectionDraftRecord>().Select(value => value.Id).ToList().ToHashSet();
        if (rows.Any(value => !connections.Contains(value.ConnectionId)))
        { throw new ArgumentException("凭证待补标记引用了不存在的连接。"); }
        client.Deleteable<CredentialRequirementRecord>().ExecuteCommand();
        foreach (var chunk in rows.Chunk(100))
        { client.Insertable(chunk).ExecuteCommand(); }
    }

    public static void RequireComplete(SqlSugarClient client)
    {
        if (client.Ado.GetInt("""
            SELECT EXISTS(SELECT 1 FROM configuration_credential_requirement r
                INNER JOIN connection_draft d ON d.id = r.connection_id)
            """) != 0)
        { throw new DraftWriteException("configuration.credentialsRequired"); }
    }

    public static bool CompleteWithNewSecret(SqlSugarClient client, long connectionId, string? account, TimeProvider time)
    {
        if (!Remove(client, connectionId))
        { return false; }
        ConfigurationEditJournal.Record(client, "connection", connectionId, account, time);
        ConfigurationDraft.Advance(client, time);
        return true;
    }

    public static bool Remove(SqlSugarClient client, long connectionId) =>
        client.Deleteable<CredentialRequirementRecord>().Where(value => value.ConnectionId == connectionId).ExecuteCommand() != 0;
}

[SugarTable("configuration_credential_requirement")]
internal sealed class CredentialRequirementRecord
{
    [SugarColumn(ColumnName = "connection_id", IsPrimaryKey = true)]
    public long ConnectionId { get; set; }
    [SugarColumn(ColumnName = "account_identity")]
    public string AccountIdentity { get; set; } = string.Empty;
    [SugarColumn(ColumnName = "reason")]
    public string Reason { get; set; } = string.Empty;
}
