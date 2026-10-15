using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.DataProtection;
using SqlSugar;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Adapters.Diagnostics;
using TenonEdge.Adapters.Mqtt;
using TenonEdge.Host.Features.Audit;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Security;
using TenonEdge.Host.Storage;

namespace TenonEdge.Host.Features.Credentials;

/// <summary>连接信任独立于认证凭证，按精确接收方绑定。没有全局信任开关，不修改操作系统证书库。
/// 每次调用只持有短存储上下文；有效快照/队列不携带证书或信任版本。</summary>
internal sealed class ConnectionTrustStore(ConfigDatabase database, IDataProtectionProvider protection, AuditTrail audit, TimeProvider time) : ITlsTrustResolver
{
    public TlsTrustSnapshot? ResolveTlsTrust(long connectionId, string recipient)
    {
        using var client = database.CreateIsolatedClient();
        var row = client.Queryable<ConnectionTrustRecord>().InSingle(connectionId);
        return row is not null && row.Recipient == recipient ? Decode(row) : null;
    }

    public ConnectionTrustMetadata? Read(long connectionId)
    {
        using var client = database.CreateIsolatedClient();
        var row = client.Queryable<ConnectionTrustRecord>().InSingle(connectionId);
        return row is null ? null : Metadata(row);
    }

    public ConnectionTrustMetadata Write(long connectionId, ConnectionTrustRequest request, ManagementActor actor)
    {
        var version = Guid.NewGuid().ToString("N");
        var info = ServerCertificateTrust.Validate(new(request.Mode, request.CertificateDerBase64, version));
        if (!request.Confirmed || request.ConfirmedFingerprint != info.Sha256Fingerprint ||
            info.NotBefore > time.GetUtcNow() || info.NotAfter < time.GetUtcNow())
        { throw new ArgumentException("请核对有效证书的 SHA-256 指纹并明确确认。"); }
        return database.InTransaction(client =>
        {
            var now = time.GetUtcNow().ToUnixTimeMilliseconds();
            var account = actor.RequireAdministrator(client, now).Audit;
            var recipient = RequireDraft(client, connectionId, request.ExpectedDraftVersion, request.Recipient);
            var current = client.Queryable<ConnectionTrustRecord>().InSingle(connectionId);
            if (current?.Version != request.ExpectedVersion)
            { throw new CredentialWriteException("trust.versionConflict"); }
            var row = new ConnectionTrustRecord
            {
                ConnectionId = connectionId,
                Recipient = recipient,
                Mode = (int)request.Mode,
                Version = version,
                UpdatedAt = now,
            };
            row.ProtectedCertificate = Protector(row).Protect(info.CertificateDerBase64);
            if (current is null)
            { client.Insertable(row).ExecuteCommand(); }
            else
            { client.Updateable(row).ExecuteCommand(); }
            audit.Append(client, new AuditEntry("connection.trust.write", AuditResults.Success, account, actor.SourceIp,
                connectionId.ToString(CultureInfo.InvariantCulture), $"mode={request.Mode}; sha256={info.Sha256Fingerprint}; version={version}"));
            return Metadata(row);
        });
    }

    public void Clear(long connectionId, ConnectionTrustClearRequest request, ManagementActor actor) => database.InTransaction(client =>
    {
        var account = actor.RequireAdministrator(client, time.GetUtcNow().ToUnixTimeMilliseconds()).Audit;
        _ = RequireDraft(client, connectionId, request.ExpectedDraftVersion, request.Recipient);
        if (!request.Confirmed)
        { throw new ArgumentException("请明确确认清除连接信任。"); }
        CredentialDraftGuard.RequireUnreferenced(client, connectionId);
        var row = client.Queryable<ConnectionTrustRecord>().InSingle(connectionId);
        if (row is null || row.Version != request.ExpectedVersion)
        { throw new CredentialWriteException("trust.versionConflict"); }
        client.Deleteable(row).ExecuteCommand();
        audit.Append(client, new AuditEntry("connection.trust.clear", AuditResults.Success, account, actor.SourceIp,
            connectionId.ToString(CultureInfo.InvariantCulture), "已清除未被运行版本引用的连接信任，后续握手使用系统信任。"));
        return true;
    });

    private static string RequireDraft(SqlSugarClient client, long id, string expectedDraftVersion, string recipient)
    {
        var draft = ConnectionDraftStore.Read(client, id) ?? throw new CredentialWriteException("credential.connectionNotFound");
        var target = draft.Kind switch
        {
            ConnectionKind.Http when new Uri(draft.Http!.BaseAddress).Scheme == "https" => HttpCredentialStore.NormalizeRecipient(draft.Http.BaseAddress),
            ConnectionKind.Mqtt when draft.Mqtt!.Tls => new MqttConnectionSettings { Host = draft.Mqtt.Host, Port = draft.Mqtt.Port, Tls = true }.Recipient,
            _ => throw new CredentialWriteException("trust.tlsRequired"),
        };
        if (string.IsNullOrWhiteSpace(recipient) || recipient != target)
        { throw new CredentialWriteException("credential.recipientMismatch"); }
        CredentialDraftGuard.Check(client, id, draft.Kind, recipient, expectedDraftVersion, requireDraft: true);
        return target;
    }

    private IDataProtector Protector(ConnectionTrustRecord row) => protection.CreateProtector(SecretProtection.Purposes.ConnectionTrust,
        row.ConnectionId.ToString(CultureInfo.InvariantCulture), row.Recipient, row.Mode.ToString(CultureInfo.InvariantCulture));

    private TlsTrustSnapshot Decode(ConnectionTrustRecord row)
    {
        string certificate;
        try
        { certificate = Protector(row).Unprotect(row.ProtectedCertificate); }
        catch (CryptographicException) { certificate = ""; } // 非 null 无效快照，握手拒绝；不能回退到系统信任。
        return new((TlsTrustMode)row.Mode, certificate, row.Version);
    }

    private ConnectionTrustMetadata Metadata(ConnectionTrustRecord row)
    {
        ServerCertificateInfo? info = null;
        try
        { info = ServerCertificateTrust.Validate(Decode(row)); }
        catch (Exception exception) when (exception is ArgumentException or FormatException or CryptographicException) { }
        return new(row.ConnectionId, row.Recipient, (TlsTrustMode)row.Mode, row.Version, info is not null,
            info?.Subject, info?.Issuer, info?.NotBefore, info?.NotAfter, info?.Sha256Fingerprint, DateTimeOffset.FromUnixTimeMilliseconds(row.UpdatedAt));
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ConnectionTrustRequest(string ExpectedDraftVersion, string Recipient, TlsTrustMode Mode,
    string CertificateDerBase64, string ConfirmedFingerprint, bool Confirmed, string? ExpectedVersion = null)
{
    public override string ToString() => "Connection trust write (certificate and recipient omitted)";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ConnectionTrustClearRequest(string ExpectedDraftVersion, string Recipient, string ExpectedVersion, bool Confirmed);

internal sealed record ConnectionTrustMetadata(long ConnectionId, string Recipient, TlsTrustMode Mode, string Version, bool Available,
    string? Subject, string? Issuer, DateTimeOffset? NotBefore, DateTimeOffset? NotAfter, string? Sha256Fingerprint, DateTimeOffset UpdatedAt);

[SugarTable("connection_trust")]
internal sealed class ConnectionTrustRecord
{
    [SugarColumn(ColumnName = "connection_id", IsPrimaryKey = true)]
    public long ConnectionId { get; set; }
    [SugarColumn(ColumnName = "recipient")]
    public string Recipient { get; set; } = "";
    [SugarColumn(ColumnName = "mode")]
    public int Mode { get; set; }
    [SugarColumn(ColumnName = "protected_certificate")]
    public string ProtectedCertificate { get; set; } = "";
    [SugarColumn(ColumnName = "version")]
    public string Version { get; set; } = "";
    [SugarColumn(ColumnName = "updated_at")]
    public long UpdatedAt { get; set; }
}
