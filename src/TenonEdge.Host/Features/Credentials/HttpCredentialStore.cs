using System.Globalization;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using SqlSugar;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Adapters.Http;
using TenonEdge.Host.Features.Audit;
using TenonEdge.Host.Features.Configuration;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Security;
using TenonEdge.Host.Storage;
using TenonEdge.Runtime.Deliveries;

namespace TenonEdge.Host.Features.Credentials;

/// <summary>HTTP 当前凭证按连接和基础地址绑定；单例不保留数据库客户端或活跃事务。</summary>
internal sealed class HttpCredentialStore(
    ConfigDatabase database, IDataProtectionProvider protection, DeliveryCache deliveries, AuditTrail audit, TimeProvider time) : IHttpCredentialResolver
{
    public HttpCredentialIdentity? DescribeHttp(long connectionId)
    {
        using var client = database.CreateIsolatedClient();
        var record = client.Queryable<HttpCredentialRecord>().InSingle(connectionId);
        return record is null ? null : Identity(record);
    }

    public HttpCredential? ResolveHttp(long connectionId, string recipient)
    {
        using var client = database.CreateIsolatedClient();
        var record = client.Queryable<HttpCredentialRecord>().InSingle(connectionId);
        if (record?.ProtectedSecret is null || record.Recipient != recipient)
        { return null; }
        try
        { return new(Identity(record), Protector(connectionId).Unprotect(record.ProtectedSecret)); }
        catch (CryptographicException) { return null; }
    }

    public HttpCredentialMetadata? Read(long connectionId)
    {
        using var client = database.CreateIsolatedClient();
        var record = client.Queryable<HttpCredentialRecord>().InSingle(connectionId);
        return record is null ? null : Metadata(record);
    }

    public async Task<HttpCredentialMetadata> WriteAsync(long connectionId, HttpCredentialRequest request, AuditAccount accountName, string? sourceIp, bool administrator, CancellationToken token = default)
    {
        if (connectionId is < 1 or > 9007199254740991L)
        { throw new ArgumentException("连接 ID 超出安全整数范围。"); }
        var recipient = NormalizeRecipient(request.Recipient);
        if (request.RetainExisting && !administrator)
        { throw new CredentialWriteException("credential.administratorRequired"); }
        string? certificateSubject = null;
        if (request.Certificate is not null || request.Kind == HttpCredentialKind.ClientCertificate)
        {
            if (request.Kind != HttpCredentialKind.ClientCertificate || request.Certificate is null || request.Secret is not null ||
                request.Username is not null || request.Name is not null || request.RetainExisting || !recipient.StartsWith("https://", StringComparison.Ordinal))
            { throw new ArgumentException("客户端证书仅用于 HTTPS，须单独提交证书材料，不可混用其他认证。"); }
            using var material = request.Certificate.Open(time.GetUtcNow());
            certificateSubject = material.Info.Subject;
            var data = material.ExportPkcs12();
            try
            { request = request with { Secret = Convert.ToBase64String(data), Certificate = null }; }
            finally { CryptographicOperations.ZeroMemory(data); }
        }
        if (request.Secret is not null)
        {
            if (request.RetainExisting || request.Kind is null)
            { throw new ArgumentException("填写秘密时须填写认证类型，不能同时保留旧凭证。"); }
            HttpCredentialValidation.Validate(request.Kind.Value, request.Username, request.Name, request.Secret);
        }
        else if (request.Kind is not null || request.Username is not null || request.Name is not null || request.AccountChanged is not null)
        { throw new ArgumentException("认证类型、用户名、参数名和账户变更声明须与新凭证一起提交。"); }
        // 先阻止本连接的新冻结，等已有发布得到写者结果；此时还没有打开配置事务。
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5), time);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        IDisposable change;
        try
        { change = await deliveries.IdentityBarrier.EnterChangeAsync(connectionId, operation.Token); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new CredentialWriteException("credential.publicationBusy"); }
        using var changeLease = change;
        token.ThrowIfCancellationRequested();
        if (deliveries.GetStatus().Store is null)
        { throw new CredentialWriteException("credential.queueUnavailable"); }
        var hasPending = deliveries.HasUndelivered(connectionId);
        return database.InTransaction(client =>
        {
            CredentialDraftGuard.Check(client, connectionId, ConnectionKind.Http, recipient, request.ExpectedDraftVersion);
            if (client.Queryable<MqttCredentialRecord>().InSingle(connectionId) is not null)
            { throw new CredentialWriteException("credential.protocolConflict"); }
            var record = client.Queryable<HttpCredentialRecord>().InSingle(connectionId);
            if (record?.Version != request.ExpectedVersion)
            { throw new CredentialWriteException("credential.versionConflict"); }
            if (request.RetainExisting && record?.ProtectedSecret is null)
            { throw new ArgumentException("保留操作要求原凭证可用。"); }
            if (request.RetainExisting && record?.Kind == (int)HttpCredentialKind.ClientCertificate && !recipient.StartsWith("https://", StringComparison.Ordinal))
            { throw new ArgumentException("客户端证书认证不能保留到明文 HTTP 地址。"); }
            if (record is null && request.Secret is null)
            { throw new ArgumentException("首次写入须提供认证类型和秘密。"); }
            if (request.Secret is not null && record is not null && hasPending &&
                record.Kind == (int)request.Kind!.Value &&
                (request.Kind != HttpCredentialKind.Basic || record.Username == request.Username) &&
                (request.Kind != HttpCredentialKind.ClientCertificate || record.CertificateSubject == certificateSubject) && request.AccountChanged is null)
            { throw new CredentialWriteException("credential.accountDeclarationRequired"); }
            var existed = record is not null;
            record ??= new HttpCredentialRecord { ConnectionId = connectionId, AccountMarker = Guid.NewGuid().ToString("N") };
            var changedRecipient = existed && record.Recipient != recipient;
            if (changedRecipient && !request.RetainExisting)
            { record.ProtectedSecret = null; }
            if (request.Secret is not null)
            {
                record.Kind = (int)request.Kind!.Value;
                record.Username = request.Username;
                record.Name = request.Name;
                record.CertificateSubject = certificateSubject;
                record.ProtectedSecret = Protector(connectionId).Protect(request.Secret);
                if (request.AccountChanged == true)
                { record.AccountMarker = Guid.NewGuid().ToString("N"); }
            }
            record.Recipient = recipient;
            record.Version = Guid.NewGuid().ToString("N");
            record.UpdatedAt = time.GetUtcNow().ToUnixTimeMilliseconds();
            if (existed)
            { client.Updateable(record).ExecuteCommand(); }
            else
            { client.Insertable(record).ExecuteCommand(); }
            if (request.Secret is not null)
            { ConfigurationCredentialRequirements.CompleteWithNewSecret(client, connectionId, accountName.Name, time); }
            audit.Append(client, new AuditEntry("credential.http.write", AuditResults.Success, accountName, sourceIp,
                connectionId.ToString(CultureInfo.InvariantCulture),
                $"recipientChanged={changedRecipient}; retained={request.RetainExisting}; accountChanged={request.AccountChanged}; invalidated={record.ProtectedSecret is null}"));
            return Metadata(record);
        });
    }

    public void InvalidateChangedRecipients(ISqlSugarClient client, RuntimePlanDocument plan)
    {
        foreach (var settings in plan.Units.SelectMany(RuntimeConnectionReferences.ReadHttp))
        {
            var record = client.Queryable<HttpCredentialRecord>().InSingle(settings.ConnectionId);
            if (record is null || record.Recipient == settings.BaseAddress)
            { continue; }
            record.Recipient = settings.BaseAddress;
            record.ProtectedSecret = null;
            record.Version = Guid.NewGuid().ToString("N");
            record.UpdatedAt = time.GetUtcNow().ToUnixTimeMilliseconds();
            client.Updateable(record).ExecuteCommand();
            audit.Append(client, new AuditEntry("credential.http.invalidate", AuditResults.Success, RuntimeVersionStore.SystemOperator, null,
                record.ConnectionId.ToString(CultureInfo.InvariantCulture), "新版本基础接收方变化，默认作废保存的认证凭证。"));
        }
    }

    /// <summary>明确清除与接收方变化导致的“作废”不同：只有未被保留运行版本引用的草稿可清除。</summary>
    public void Clear(long connectionId, string expectedDraftVersion, string expectedVersion, AuditAccount account, string? sourceIp) =>
        database.InTransaction(client =>
        {
            CredentialDraftGuard.Check(client, connectionId, ConnectionKind.Http, null, expectedDraftVersion, requireDraft: true);
            CredentialDraftGuard.RequireUnreferenced(client, connectionId);
            var record = client.Queryable<HttpCredentialRecord>().InSingle(connectionId);
            if (record is null || record.Version != expectedVersion)
            { throw new CredentialWriteException("credential.versionConflict"); }
            client.Deleteable(record).ExecuteCommand();
            audit.Append(client, new AuditEntry("credential.http.clear", AuditResults.Success, account, sourceIp,
                connectionId.ToString(CultureInfo.InvariantCulture), "已明确清除未被运行版本引用的认证凭证。"));
            return true;
        });

    internal static string NormalizeRecipient(string recipient)
    {
        if (string.IsNullOrWhiteSpace(recipient) || !Uri.TryCreate(recipient, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            string.IsNullOrEmpty(uri.Host) || uri.Port < 1 || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
        { throw new ArgumentException("接收方须为不含凭证、查询参数和片段的 HTTP(S) 基础地址。"); }
        return uri.AbsoluteUri.TrimEnd('/') + "/";
    }

    private IDataProtector Protector(long connectionId) => protection.CreateProtector(SecretProtection.Purposes.HttpCredential, connectionId.ToString(CultureInfo.InvariantCulture));
    private static HttpCredentialIdentity Identity(HttpCredentialRecord record) => new((HttpCredentialKind)record.Kind, record.Username, record.Name, record.AccountMarker, record.Version, record.CertificateSubject);
    private static HttpCredentialMetadata Metadata(HttpCredentialRecord record) => new(record.ConnectionId, record.Recipient, record.Version, record.ProtectedSecret is not null, DateTimeOffset.FromUnixTimeMilliseconds(record.UpdatedAt));
}

internal sealed record HttpCredentialRequest(string Recipient, HttpCredentialKind? Kind = null, string? Username = null, string? Name = null, string? Secret = null,
    string? ExpectedVersion = null, bool? AccountChanged = null, bool RetainExisting = false, string? ExpectedDraftVersion = null, ClientCertificateUpload? Certificate = null)
{
    public override string ToString() => "HTTP credential write request (secret omitted)";
}
internal sealed record HttpCredentialMetadata(long ConnectionId, string Recipient, string Version, bool Configured, DateTimeOffset UpdatedAt);
