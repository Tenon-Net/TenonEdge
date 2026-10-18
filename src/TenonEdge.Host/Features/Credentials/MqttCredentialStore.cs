using System.Globalization;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using SqlSugar;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Adapters.Mqtt;
using TenonEdge.Host.Features.Audit;
using TenonEdge.Host.Features.Configuration;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Security;
using TenonEdge.Host.Storage;
using TenonEdge.Runtime.Deliveries;

namespace TenonEdge.Host.Features.Credentials;

/// <summary>当前 MQTT 认证凭证：不进入快照或 queue.db。单例只保留数据库工厂和保护器，不保留客户端或事务。</summary>
internal sealed class MqttCredentialStore(
    ConfigDatabase database, IDataProtectionProvider protection, DeliveryCache deliveries, AuditTrail audit, TimeProvider time) : ICredentialResolver
{
    public MqttCredentialIdentity? DescribeMqtt(long connectionId)
    {
        using var client = database.CreateIsolatedClient();
        var record = client.Queryable<MqttCredentialRecord>().InSingle(connectionId);
        return record is null ? null : Identity(record);
    }

    public MqttCredential? ResolveMqtt(long connectionId, string recipient)
    {
        using var client = database.CreateIsolatedClient();
        var record = client.Queryable<MqttCredentialRecord>().InSingle(connectionId);
        if (record?.ProtectedPassword is null || record.Recipient != recipient)
        {
            return null;
        }

        try
        {
            return new MqttCredential(record.Username, Protector(connectionId).Unprotect(record.ProtectedPassword), record.AccountMarker, record.Version, (MqttCredentialKind)record.Kind, record.CertificateSubject);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    public MqttCredentialMetadata? Read(long connectionId)
    {
        using var client = database.CreateIsolatedClient();
        var record = client.Queryable<MqttCredentialRecord>().InSingle(connectionId);
        return record is null ? null : Metadata(record);
    }

    public async Task<MqttCredentialMetadata> WriteAsync(long connectionId, MqttCredentialRequest request, AuditAccount accountName, string? sourceIp, bool administrator, CancellationToken token = default)
    {
        if (connectionId is < 1 or > 9007199254740991L)
        {
            throw new ArgumentException("连接 ID 超出安全整数范围。");
        }

        var recipient = NormalizeRecipient(request.Recipient);
        if (request.RetainExisting && !administrator)
        {
            throw new CredentialWriteException("credential.administratorRequired");
        }

        string? certificateSubject = null;
        if (request.Certificate is not null || request.Kind == MqttCredentialKind.ClientCertificate)
        {
            if (request.Kind != MqttCredentialKind.ClientCertificate || request.Certificate is null || request.Password is not null ||
                request.Username is not null || request.RetainExisting || !recipient.StartsWith("mqtts://", StringComparison.Ordinal))
            { throw new ArgumentException("客户端证书仅用于 MQTT TLS，须单独提交证书材料，不可混用账号密码。"); }
            using var material = request.Certificate.Open(time.GetUtcNow());
            certificateSubject = material.Info.Subject;
            var data = material.ExportPkcs12();
            try
            { request = request with { Password = Convert.ToBase64String(data), Username = string.Empty, Certificate = null }; }
            finally { CryptographicOperations.ZeroMemory(data); }
        }
        if (request.Password is not null)
        {
            if (request.RetainExisting)
            { throw new ArgumentException("填写新凭证时不能同时保留旧凭证。"); }
            request = request with { Kind = request.Kind ?? MqttCredentialKind.Password };
            MqttCredentialValidation.Validate(new(request.Username!, request.Password, "validation", "validation", request.Kind.Value, certificateSubject),
                recipient.StartsWith("mqtts://", StringComparison.Ordinal));
        }
        else if (request.Kind is not null || request.Username is not null || request.AccountChanged is not null)
        { throw new ArgumentException("认证类型、用户名和账户变更声明须与新凭证一起提交。"); }

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
        {
            throw new CredentialWriteException("credential.queueUnavailable");
        }

        var hasPending = deliveries.HasUndelivered(connectionId);
        return database.InTransaction(client =>
        {
            CredentialDraftGuard.Check(client, connectionId, ConnectionKind.Mqtt, recipient, request.ExpectedDraftVersion);
            if (client.Queryable<HttpCredentialRecord>().InSingle(connectionId) is not null)
            { throw new CredentialWriteException("credential.protocolConflict"); }
            var record = client.Queryable<MqttCredentialRecord>().InSingle(connectionId);
            if (record?.Version != request.ExpectedVersion)
            {
                throw new CredentialWriteException("credential.versionConflict");
            }

            if (request.RetainExisting && (record?.ProtectedPassword is null || request.Password is not null || request.Username is not null || request.AccountChanged is not null))
            {
                throw new ArgumentException("保留操作仅可更改接收方绑定，且原凭证须可用。");
            }

            if (request.RetainExisting && record?.Kind == (int)MqttCredentialKind.ClientCertificate && !recipient.StartsWith("mqtts://", StringComparison.Ordinal))
            { throw new ArgumentException("客户端证书不能保留到明文 MQTT 地址。"); }
            if (record is null && request.Password is null)
            {
                throw new ArgumentException("首次写入须填写用户名和密码。");
            }

            if (request.Password is not null && record is not null && hasPending && record.Kind == (int)request.Kind!.Value &&
                (request.Kind == MqttCredentialKind.ClientCertificate ? record.CertificateSubject == certificateSubject : record.Username == request.Username) && request.AccountChanged is null)
            {
                throw new CredentialWriteException("credential.accountDeclarationRequired");
            }

            if (request.Password is null && !request.RetainExisting && request.Username is not null)
            {
                throw new ArgumentException("用户名与密码须一起更新。");
            }

            var existed = record is not null;
            record ??= new MqttCredentialRecord { ConnectionId = connectionId, AccountMarker = Guid.NewGuid().ToString("N") };
            var changedRecipient = existed && record.Recipient != recipient;
            if (changedRecipient && !request.RetainExisting)
            {
                record.ProtectedPassword = null;
            }

            if (request.Password is not null)
            {
                record.Kind = (int)request.Kind!.Value;
                record.CertificateSubject = certificateSubject;
                record.Username = request.Username!;
                record.ProtectedPassword = Protector(connectionId).Protect(request.Password);
                if (request.AccountChanged == true)
                {
                    record.AccountMarker = Guid.NewGuid().ToString("N");
                }
            }

            record.Recipient = recipient;
            record.Version = Guid.NewGuid().ToString("N");
            record.UpdatedAt = time.GetUtcNow().ToUnixTimeMilliseconds();
            if (existed)
            {
                client.Updateable(record).ExecuteCommand();
            }
            else
            {
                client.Insertable(record).ExecuteCommand();
            }

            if (request.Password is not null)
            { ConfigurationCredentialRequirements.CompleteWithNewSecret(client, connectionId, accountName.Name, time); }
            audit.Append(client, new AuditEntry("credential.mqtt.write", AuditResults.Success, accountName, sourceIp,
                connectionId.ToString(CultureInfo.InvariantCulture),
                $"recipientChanged={changedRecipient}; retained={request.RetainExisting}; accountChanged={request.AccountChanged}; invalidated={record.ProtectedPassword is null}"));
            return Metadata(record);
        });
    }

    /// <summary>随新版本提交默认作废不匹配的旧绑定；管理员此前显式保留的绑定已经匹配，不再作废。</summary>
    public void InvalidateChangedRecipients(ISqlSugarClient client, RuntimePlanDocument plan)
    {
        foreach (var settings in plan.Units.SelectMany(RuntimeConnectionReferences.ReadMqtt))
        {
            var record = client.Queryable<MqttCredentialRecord>().InSingle(settings.ConnectionId);
            if (record is null || record.Recipient == settings.Recipient)
            {
                continue;
            }

            record.Recipient = settings.Recipient;
            record.ProtectedPassword = null;
            record.Version = Guid.NewGuid().ToString("N");
            record.UpdatedAt = time.GetUtcNow().ToUnixTimeMilliseconds();
            client.Updateable(record).ExecuteCommand();
            audit.Append(client, new AuditEntry("credential.mqtt.invalidate", AuditResults.Success, RuntimeVersionStore.SystemOperator, null,
                record.ConnectionId.ToString(CultureInfo.InvariantCulture), "新版本接收方变化，默认作废保存的认证凭证。"));
        }
    }

    /// <summary>明确清除与接收方变化导致的“作废”不同：只有未被保留运行版本引用的草稿可清除。</summary>
    public void Clear(long connectionId, string expectedDraftVersion, string expectedVersion, AuditAccount account, string? sourceIp) =>
        database.InTransaction(client =>
        {
            CredentialDraftGuard.Check(client, connectionId, ConnectionKind.Mqtt, null, expectedDraftVersion, requireDraft: true);
            CredentialDraftGuard.RequireUnreferenced(client, connectionId);
            var record = client.Queryable<MqttCredentialRecord>().InSingle(connectionId);
            if (record is null || record.Version != expectedVersion)
            { throw new CredentialWriteException("credential.versionConflict"); }
            client.Deleteable(record).ExecuteCommand();
            audit.Append(client, new AuditEntry("credential.mqtt.clear", AuditResults.Success, account, sourceIp,
                connectionId.ToString(CultureInfo.InvariantCulture), "已明确清除未被运行版本引用的认证凭证。"));
            return true;
        });

    internal static string NormalizeRecipient(string recipient)
    {
        if (string.IsNullOrWhiteSpace(recipient) || !Uri.TryCreate(recipient, UriKind.Absolute, out var uri) || uri.Scheme is not ("mqtt" or "mqtts") ||
            string.IsNullOrEmpty(uri.Host) || uri.Port is < 1 or > 65535 || uri.UserInfo.Length != 0 || uri.AbsolutePath is not ("" or "/") || uri.Query.Length != 0 || uri.Fragment.Length != 0)
        {
            throw new ArgumentException("接收方须为 mqtt[s]://主机:端口，不得包含凭证、路径或查询参数。");
        }

        return $"{uri.Scheme}://{uri.Host.ToLowerInvariant()}:{uri.Port}";
    }

    private IDataProtector Protector(long connectionId) => protection.CreateProtector(SecretProtection.Purposes.MqttCredential, connectionId.ToString(CultureInfo.InvariantCulture));
    private static MqttCredentialIdentity Identity(MqttCredentialRecord record) => new(record.Username, record.AccountMarker, record.Version, (MqttCredentialKind)record.Kind, record.CertificateSubject);
    private static MqttCredentialMetadata Metadata(MqttCredentialRecord record) => new(record.ConnectionId, record.Recipient, record.Version, record.ProtectedPassword is not null, DateTimeOffset.FromUnixTimeMilliseconds(record.UpdatedAt));
}

internal sealed record MqttCredentialRequest(string Recipient, string? Username = null, string? Password = null,
    string? ExpectedVersion = null, bool? AccountChanged = null, bool RetainExisting = false, string? ExpectedDraftVersion = null, MqttCredentialKind? Kind = null, ClientCertificateUpload? Certificate = null)
{
    public override string ToString() => "MQTT credential write request (secret omitted)";
}
internal sealed record MqttCredentialMetadata(long ConnectionId, string Recipient, string Version, bool Configured, DateTimeOffset UpdatedAt);
internal sealed class CredentialWriteException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
