using System.Globalization;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using SqlSugar;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Adapters.Mqtt;
using TenonEdge.Host.Features.Audit;
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
        return record is null ? null : new MqttCredentialIdentity(record.Username, record.AccountMarker, record.Version);
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
            return new MqttCredential(record.Username, Protector(connectionId).Unprotect(record.ProtectedPassword), record.AccountMarker, record.Version);
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

    public MqttCredentialMetadata Write(long connectionId, MqttCredentialRequest request, string? accountName, string? sourceIp, bool administrator)
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

        if (request.Password is { Length: > 4096 } || request.Password?.Contains('\0', StringComparison.Ordinal) == true || request.Username is { Length: > 256 } || request.Username?.Contains('\0', StringComparison.Ordinal) == true)
        {
            throw new ArgumentException("用户名或密码超出长度限制或包含空字符。");
        }

        if (request.Password is not null && (request.RetainExisting || string.IsNullOrEmpty(request.Username)))
        {
            throw new ArgumentException("填写密码时须填写用户名，不能同时选择保留旧凭证。");
        }

        if (request.Password is null && request.AccountChanged is not null)
        {
            throw new ArgumentException("账户变更声明须与新凭证一起提交。");
        }

        // 待发和失败区记录都属于尚未交付的数据；跨库管理协同随 T3 补齐。没有在事务中等待网络。
        if (deliveries.GetStatus().Store is null)
        {
            throw new CredentialWriteException("credential.queueUnavailable");
        }

        var hasPending = deliveries.ReadPending(connectionId, 0, 1).Count > 0 || deliveries.ReadFailed(connectionId, 0, 1).Count > 0;
        return database.InTransaction(client =>
        {
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

            if (record is null && request.Password is null)
            {
                throw new ArgumentException("首次写入须填写用户名和密码。");
            }

            if (request.Password is not null && record is not null && hasPending && record.Username == request.Username && request.AccountChanged is null)
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

            audit.Append(client, new AuditEntry("credential.mqtt.write", AuditResults.Success, accountName, sourceIp,
                connectionId.ToString(CultureInfo.InvariantCulture),
                $"recipientChanged={changedRecipient}; retained={request.RetainExisting}; accountChanged={request.AccountChanged}; invalidated={record.ProtectedPassword is null}"));
            return Metadata(record);
        });
    }

    /// <summary>随新版本提交默认作废不匹配的旧绑定；管理员此前显式保留的绑定已经匹配，不再作废。</summary>
    public void InvalidateChangedRecipients(ISqlSugarClient client, RuntimePlanDocument plan)
    {
        foreach (var unit in plan.Units.Where(unit => unit.Id.Kind is "input.mqtt" or "output.mqtt"))
        {
            MqttConnectionSettings settings = unit.Id.Kind == "input.mqtt"
                ? MqttInputSettings.Parse(unit.Configuration) : MqttOutputSettings.Parse(unit.Configuration);
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
    private static MqttCredentialMetadata Metadata(MqttCredentialRecord record) => new(record.ConnectionId, record.Recipient, record.Version, record.ProtectedPassword is not null, DateTimeOffset.FromUnixTimeMilliseconds(record.UpdatedAt));
}

internal sealed record MqttCredentialRequest(string Recipient, string? Username = null, string? Password = null,
    string? ExpectedVersion = null, bool? AccountChanged = null, bool RetainExisting = false);
internal sealed record MqttCredentialMetadata(long ConnectionId, string Recipient, string Version, bool Configured, DateTimeOffset UpdatedAt);
internal sealed class CredentialWriteException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
