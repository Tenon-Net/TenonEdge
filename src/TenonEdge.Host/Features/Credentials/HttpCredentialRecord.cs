using SqlSugar;

namespace TenonEdge.Host.Features.Credentials;

[SugarTable(TableName)]
internal sealed class HttpCredentialRecord
{
    public const string TableName = "http_credential";

    [SugarColumn(ColumnName = "connection_id", IsPrimaryKey = true)]
    public long ConnectionId { get; set; }
    [SugarColumn(ColumnName = "recipient")]
    public string Recipient { get; set; } = string.Empty;
    [SugarColumn(ColumnName = "username")]
    public string? Username { get; set; }
    [SugarColumn(ColumnName = "kind")]
    public int Kind { get; set; }
    [SugarColumn(ColumnName = "name")]
    public string? Name { get; set; }
    [SugarColumn(ColumnName = "protected_secret")]
    public string? ProtectedSecret { get; set; }
    [SugarColumn(ColumnName = "account_marker")]
    public string AccountMarker { get; set; } = string.Empty;
    [SugarColumn(ColumnName = "version")]
    public string Version { get; set; } = string.Empty;
    [SugarColumn(ColumnName = "updated_at")]
    public long UpdatedAt { get; set; }
}
