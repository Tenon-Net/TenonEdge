using SqlSugar;

namespace TenonEdge.Host.Features.Credentials;

[SugarTable(TableName)]
internal sealed class MqttCredentialRecord
{
    public const string TableName = "mqtt_credential";

    [SugarColumn(ColumnName = "connection_id", IsPrimaryKey = true)]
    public long ConnectionId { get; set; }
    [SugarColumn(ColumnName = "recipient")]
    public string Recipient { get; set; } = string.Empty;
    [SugarColumn(ColumnName = "username")]
    public string Username { get; set; } = string.Empty;
    [SugarColumn(ColumnName = "kind")]
    public int Kind { get; set; }
    [SugarColumn(ColumnName = "certificate_subject")]
    public string? CertificateSubject { get; set; }
    [SugarColumn(ColumnName = "protected_password")]
    public string? ProtectedPassword { get; set; }
    [SugarColumn(ColumnName = "account_marker")]
    public string AccountMarker { get; set; } = string.Empty;
    [SugarColumn(ColumnName = "version")]
    public string Version { get; set; } = string.Empty;
    [SugarColumn(ColumnName = "updated_at")]
    public long UpdatedAt { get; set; }
}
