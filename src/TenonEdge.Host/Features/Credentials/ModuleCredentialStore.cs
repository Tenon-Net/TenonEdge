using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.DataProtection;
using SqlSugar;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Parameters;
using TenonEdge.Host.Features.Audit;
using TenonEdge.Host.Features.Configuration;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Security;
using TenonEdge.Host.Storage;
using TenonEdge.Runtime.Connections;
using TenonEdge.Runtime.Deliveries;

namespace TenonEdge.Host.Features.Credentials;

/// <summary>一个连接一份受保护材料。仅保存到独立凭证表；当前秘密不属于草稿/冻结计划。
/// 运行解析使用短命独立客户端，不保留请求、数据库客户端或事务。</summary>
internal sealed class ModuleCredentialStore(ConfigDatabase database, IDataProtectionProvider protection,
    ConnectionModuleCatalog modules, DeliveryCache deliveries, AuditTrail audit, TimeProvider time) : IModuleCredentialResolver
{
    public ModuleCredentialIdentity? DescribeModule(long connectionId)
    {
        using var client = database.CreateIsolatedClient();
        return client.Queryable<ModuleCredentialRecord>().InSingle(connectionId) is { } row ? Identity(row) : null;
    }

    public ModuleCredential? ResolveModule(ModuleConnectionSnapshot connection)
    {
        using var client = database.CreateIsolatedClient();
        return Resolve(client.Queryable<ModuleCredentialRecord>().InSingle(connection.ConnectionId), connection);
    }

    private ModuleCredential? Resolve(ModuleCredentialRecord? record, ModuleConnectionSnapshot connection)
    {
        if (record?.ProtectedSecrets is null)
        { return null; }
        try
        {
            var definition = BoundDefinition(connection);
            var parameters = ModuleConnectionSnapshot.ReadModuleConfiguration(connection).Parameters;
            if (record.Kind != connection.Kind || record.ModuleVersion != definition.Version ||
                record.Recipient != definition.RecipientIdentity(parameters))
            { return null; }
            var values = Unprotect(record);
            if (values is null)
            { return null; }
            _ = EncodeSecrets(definition, values);
            return new(Identity(record), values);
        }
        catch (ArgumentException) { return null; }
    }

    public ModuleCredentialMetadata? Read(long id) => database.InTransaction(client =>
    {
        var row = client.Queryable<ModuleCredentialRecord>().InSingle(id);
        return row is null ? null : Metadata(client, row);
    });

    // Dynamic, non-secret review only; not a lease or authorization to save.
    // The original writer still rechecks versions and pending data under its identity barrier.
    public ModuleCredentialReview Review(long id, ModuleCredentialReviewRequest request, bool administrator)
    {
        if (request.RetainExisting && !administrator)
        { throw new CredentialWriteException("credential.administratorRequired"); }
        if (request.RetainExisting ? request.Account is not null : !ValidAccount(request.Account))
        { throw new ArgumentException("请核对可见账户。"); }
        var captured = database.InTransaction(client =>
        {
            var connection = RequireDraft(client, id, request.ExpectedDraftVersion).Module!.Snapshot(id);
            var definition = BoundDefinition(connection);
            var parameters = ModuleConnectionSnapshot.ReadModuleConfiguration(connection).Parameters;
            var row = client.Queryable<ModuleCredentialRecord>().InSingle(id);
            if (row?.Version != request.ExpectedVersion)
            { throw new CredentialWriteException("credential.versionConflict"); }
            if (client.Queryable<HttpCredentialRecord>().InSingle(id) is not null || client.Queryable<MqttCredentialRecord>().InSingle(id) is not null)
            { throw new CredentialWriteException("credential.protocolConflict"); }
            if (request.RetainExisting && row?.ProtectedSecrets is null)
            { throw new ArgumentException("没有可保留的材料。"); }
            return (Definition: definition, Recipient: definition.RecipientIdentity(parameters),
                Parameters: JsonSerializer.SerializeToElement(definition.RecipientParameters.ToDictionary(name => name, name => parameters.GetProperty(name))),
                Current: row is null ? null : Identity(row), Configured: row?.ProtectedSecrets is not null);
        });
        if (deliveries.GetStatus().Store is null)
        { throw new CredentialWriteException("credential.queueUnavailable"); }
        var report = deliveries.GetQueueReport([id], id - 1, 1);
        var usage = report.Targets.Single(value => value.ConnectionId == id).Usage;
        var account = request.RetainExisting ? captured.Current!.Account : request.Account!;
        return new(id, request.ExpectedDraftVersion, request.ExpectedVersion, captured.Definition.Kind, captured.Definition.Version,
            captured.Recipient, captured.Parameters, account, captured.Current, captured.Configured,
            captured.Current?.Recipient != captured.Recipient || captured.Current.Account != account,
            !request.RetainExisting && captured.Current?.Account == account && usage.PendingCount + usage.FailedCount > 0,
            request.RetainExisting, usage.PendingCount, usage.FailedCount, report.CapturedAt);
    }

    public async Task<ModuleCredentialMetadata> WriteAsync(long id, ModuleCredentialRequest request, AuditAccount account,
        string? sourceIp, bool administrator, CancellationToken token)
    {
        if (id is < 1 or > 9007199254740991L || string.IsNullOrWhiteSpace(request.ExpectedDraftVersion))
        { throw new ArgumentException("连接与草稿版本无效。"); }
        if (request.RetainExisting && !administrator)
        { throw new CredentialWriteException("credential.administratorRequired"); }
        if (request.Secrets is null ? request.Account is not null || request.AccountChanged is not null :
            request.RetainExisting || !ValidAccount(request.Account))
        { throw new ArgumentException("新凭证须填写可见账户，保留或作废不能混入新材料。"); }
        // Clone caller-owned values before awaiting the original queue identity barrier.
        var supplied = request.Secrets?.ToDictionary(StringComparer.Ordinal);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5), time);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        IDisposable barrier;
        try
        { barrier = await deliveries.IdentityBarrier.EnterChangeAsync(id, stop.Token); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new CredentialWriteException("credential.publicationBusy"); }
        using var lease = barrier;
        token.ThrowIfCancellationRequested();
        if (deliveries.GetStatus().Store is null)
        { throw new CredentialWriteException("credential.queueUnavailable"); }
        var pending = deliveries.HasUndelivered(id);
        return database.InTransaction(client =>
        {
            var connection = RequireDraft(client, id, request.ExpectedDraftVersion).Module!.Snapshot(id);
            var definition = BoundDefinition(connection);
            var recipient = definition.RecipientIdentity(ModuleConnectionSnapshot.ReadModuleConfiguration(connection).Parameters);
            if (client.Queryable<HttpCredentialRecord>().InSingle(id) is not null || client.Queryable<MqttCredentialRecord>().InSingle(id) is not null)
            { throw new CredentialWriteException("credential.protocolConflict"); }
            var row = client.Queryable<ModuleCredentialRecord>().InSingle(id);
            if (row?.Version != request.ExpectedVersion)
            { throw new CredentialWriteException("credential.versionConflict"); }
            if (row is null && supplied is null)
            { throw new ArgumentException("首次写入须提供凭证。"); }
            var encoded = supplied is null ? null : EncodeSecrets(definition, supplied);
            if (request.RetainExisting)
            {
                var previous = row is null ? null : Unprotect(row);
                if (previous is null)
                { throw new ArgumentException("原凭证不可用，不能保留。"); }
                encoded = EncodeSecrets(definition, previous);
            }
            if (supplied is not null && row is not null && pending && row.Account == request.Account && request.AccountChanged is null)
            { throw new CredentialWriteException("credential.accountDeclarationRequired"); }
            var existed = row is not null;
            row ??= new ModuleCredentialRecord { ConnectionId = id, AccountMarker = Guid.NewGuid().ToString("N") };
            var recipientChanged = existed && (row.Kind != connection.Kind || row.ModuleVersion != definition.Version || row.Recipient != recipient);
            if (recipientChanged && !request.RetainExisting)
            { row.ProtectedSecrets = null; }
            if (encoded is not null)
            { row.ProtectedSecrets = Protector(id).Protect(encoded); }
            if (supplied is not null)
            {
                row.Account = request.Account!;
                if (request.AccountChanged == true)
                { row.AccountMarker = Guid.NewGuid().ToString("N"); }
                ConfigurationCredentialRequirements.CompleteWithNewSecret(client, id, account.Name, time);
            }
            row.Kind = connection.Kind;
            row.ModuleVersion = definition.Version;
            row.Recipient = recipient;
            row.Version = Guid.NewGuid().ToString("N");
            row.UpdatedAt = time.GetUtcNow().ToUnixTimeMilliseconds();
            if (existed)
            { client.Updateable(row).ExecuteCommand(); }
            else
            { client.Insertable(row).ExecuteCommand(); }
            audit.Append(client, new AuditEntry("credential.module.write", AuditResults.Success, account, sourceIp,
                id.ToString(CultureInfo.InvariantCulture), $"recipientChanged={recipientChanged}; retained={request.RetainExisting}; accountChanged={request.AccountChanged}; invalidated={row.ProtectedSecrets is null}"));
            return Metadata(client, row);
        });
    }

    public void Clear(long id, string expectedDraftVersion, string expectedVersion, AuditAccount account, string? sourceIp) => database.InTransaction(client =>
    {
        _ = RequireDraft(client, id, expectedDraftVersion);
        CredentialDraftGuard.RequireUnreferenced(client, id);
        if (deliveries.HasUndelivered(id) || deliveries.ReadQuarantined(id, 0, 1).Count > 0)
        { throw new CredentialWriteException("credential.connectionReferenced"); }
        var row = client.Queryable<ModuleCredentialRecord>().InSingle(id);
        if (row is null || row.Version != expectedVersion)
        { throw new CredentialWriteException("credential.versionConflict"); }
        client.Deleteable(row).ExecuteCommand();
        audit.Append(client, new AuditEntry("credential.module.clear", AuditResults.Success, account, sourceIp,
            id.ToString(CultureInfo.InvariantCulture), "已明确清除未被保留运行版本引用且无投递记录的扩展连接凭证。"));
        return true;
    });

    public void InvalidateChangedRecipients(SqlSugarClient client, RuntimePlanDocument plan)
    {
        foreach (var connection in plan.Units.SelectMany(RuntimeConnectionReferences.ReadCustom).DistinctBy(value => value.ConnectionId))
        {
            var row = client.Queryable<ModuleCredentialRecord>().InSingle(connection.ConnectionId);
            if (row is null)
            { continue; }
            var definition = BoundDefinition(connection);
            var recipient = definition.RecipientIdentity(ModuleConnectionSnapshot.ReadModuleConfiguration(connection).Parameters);
            if (row.Kind == definition.Kind && row.ModuleVersion == definition.Version && row.Recipient == recipient)
            { continue; }
            row.Kind = definition.Kind;
            row.ModuleVersion = definition.Version;
            row.Recipient = recipient;
            row.ProtectedSecrets = null;
            row.Version = Guid.NewGuid().ToString("N");
            row.UpdatedAt = time.GetUtcNow().ToUnixTimeMilliseconds();
            client.Updateable(row).ExecuteCommand();
            audit.Append(client, new AuditEntry("credential.module.invalidate", AuditResults.Success, RuntimeVersionStore.SystemOperator, null,
                row.ConnectionId.ToString(CultureInfo.InvariantCulture), "有效连接的接收方或声明版本变化，作废旧凭证。"));
        }
    }

    private ConnectionModuleDefinition BoundDefinition(ModuleConnectionSnapshot connection)
    {
        modules.Validate(connection);
        var definition = modules.Definition(connection);
        if (definition.RecipientParameters.IsEmpty || !definition.Parameters.Parameters.Any(field => field.Type == ParameterType.Credential))
        { throw new ArgumentException("该连接未声明受保护凭证绑定。"); }
        return definition;
    }

    private static ConnectionDraft RequireDraft(SqlSugarClient client, long id, string expectedVersion)
    {
        if (ConfigurationDraft.ReadVersion(client) != expectedVersion)
        { throw new CredentialWriteException("credential.draftConflict"); }
        var draft = ConnectionDraftStore.Read(client, id) ?? throw new CredentialWriteException("credential.connectionNotFound");
        if (draft.Kind != ConnectionKind.Module || draft.Module is null)
        { throw new CredentialWriteException("credential.protocolConflict"); }
        return draft;
    }

    private ModuleCredentialMetadata Metadata(SqlSugarClient client, ModuleCredentialRecord row)
    {
        var values = Unprotect(row);
        var draft = ConnectionDraftStore.Read(client, row.ConnectionId);
        var matches = draft?.Module is { } module && Resolve(row, module.Snapshot(row.ConnectionId)) is not null;
        return new(row.ConnectionId, Identity(row), values is not null, values?.Keys.Order(StringComparer.Ordinal).ToArray() ?? [],
            matches, ConfigurationDraft.ReadVersion(client), DateTimeOffset.FromUnixTimeMilliseconds(row.UpdatedAt));
    }
    private Dictionary<string, string>? Unprotect(ModuleCredentialRecord row)
    {
        if (row.ProtectedSecrets is null)
        { return null; }
        try
        { return JsonSerializer.Deserialize<Dictionary<string, string>>(Protector(row.ConnectionId).Unprotect(row.ProtectedSecrets)); }
        catch (Exception error) when (error is CryptographicException or JsonException) { return null; }
    }
    private static string EncodeSecrets(ConnectionModuleDefinition definition, Dictionary<string, string> values)
    {
        var fields = definition.Parameters.Parameters.Where(field => field.Type == ParameterType.Credential).ToDictionary(field => field.Name, StringComparer.Ordinal);
        if (values.Count is 0 or > ParameterSchema.MaximumParameters || values.Any(pair => !fields.ContainsKey(pair.Key) || string.IsNullOrEmpty(pair.Value) || pair.Value.Length > 65536) ||
            fields.Values.Any(field => field.Required && !values.ContainsKey(field.Name)))
        { throw new ArgumentException("凭证字段不满足声明。"); }
        var json = JsonSerializer.Serialize(values);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > ParameterSchema.MaximumConfigurationBytes)
        { throw new ArgumentException("凭证材料超过大小限制。"); }
        return json;
    }
    private IDataProtector Protector(long id) => protection.CreateProtector(SecretProtection.Purposes.ModuleCredential, id.ToString(CultureInfo.InvariantCulture));
    private static bool ValidAccount(string? account) => !string.IsNullOrWhiteSpace(account) && account.Length <= 128 && !account.Any(char.IsControl);
    private static ModuleCredentialIdentity Identity(ModuleCredentialRecord row) => new(row.Kind, row.ModuleVersion, row.Recipient, row.Account, row.AccountMarker, row.Version);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ModuleCredentialRequest(string ExpectedDraftVersion, string? ExpectedVersion = null,
    Dictionary<string, string>? Secrets = null, string? Account = null, bool? AccountChanged = null, bool RetainExisting = false)
{
    public override string ToString() => "Module credential write request (private material omitted)";
}
internal sealed record ModuleCredentialMetadata(long ConnectionId, ModuleCredentialIdentity Identity, bool Configured,
    string[] ConfiguredFields, bool MatchesDraft, string DraftVersion, DateTimeOffset UpdatedAt);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ModuleCredentialReviewRequest(string ExpectedDraftVersion, string? ExpectedVersion = null,
    string? Account = null, bool RetainExisting = false);
internal sealed record ModuleCredentialReview(long ConnectionId, string ExpectedDraftVersion, string? ExpectedVersion,
    string Kind, int ModuleVersion, string Recipient, JsonElement RecipientParameters, string Account,
    ModuleCredentialIdentity? Current, bool CurrentConfigured, bool IdentityChanged, bool AccountDeclarationRequired,
    bool RetainExisting, long PendingCount, long FailedCount, DateTimeOffset CapturedAt);

[SugarTable("module_credential")]
internal sealed class ModuleCredentialRecord
{
    [SugarColumn(ColumnName = "connection_id", IsPrimaryKey = true)] public long ConnectionId { get; set; }
    [SugarColumn(ColumnName = "kind")] public string Kind { get; set; } = string.Empty;
    [SugarColumn(ColumnName = "module_version")] public int ModuleVersion { get; set; }
    [SugarColumn(ColumnName = "recipient")] public string Recipient { get; set; } = string.Empty;
    [SugarColumn(ColumnName = "account")] public string Account { get; set; } = string.Empty;
    [SugarColumn(ColumnName = "account_marker")] public string AccountMarker { get; set; } = string.Empty;
    [SugarColumn(ColumnName = "version")] public string Version { get; set; } = string.Empty;
    [SugarColumn(ColumnName = "protected_secrets")] public string? ProtectedSecrets { get; set; }
    [SugarColumn(ColumnName = "updated_at")] public long UpdatedAt { get; set; }
}
