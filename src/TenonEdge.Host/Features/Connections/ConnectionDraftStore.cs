using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using SqlSugar;
using TenonEdge.Host.Features.Audit;
using TenonEdge.Host.Features.Configuration;
using TenonEdge.Host.Features.Credentials;
using TenonEdge.Host.Features.Devices;
using TenonEdge.Host.Features.Inputs;
using TenonEdge.Host.Features.Routes;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Storage;
using TenonEdge.Host.Storage.Ids;
using TenonEdge.Runtime.Connections;
using TenonEdge.Runtime.Deliveries;

namespace TenonEdge.Host.Features.Connections;

/// <summary>非秘密连接草稿。变更、全局版本及审计在同一个短事务中提交；没有运行时或网络副作用。</summary>
internal sealed class ConnectionDraftStore(ConfigDatabase database, SnowflakeIdGenerator ids, AuditTrail audit,
    TimeProvider time, DeliveryCache deliveries, ConnectionModuleCatalog modules)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public ConnectionDraftList Read() => database.InTransaction(client => new ConnectionDraftList(
        ConfigurationDraft.ReadVersion(client), client.Queryable<ConnectionDraftRecord>().OrderBy(row => row.Id).ToList().Select(Decode).ToArray()));

    public ConnectionDeletionReview ReviewDeletion(long id, string expectedVersion)
    {
        if (id is < 1 or > 9007199254740991 || string.IsNullOrEmpty(expectedVersion) || expectedVersion.Length > 64)
        { throw new ArgumentException("连接编号或草稿版本无效。", nameof(id)); }
        var captured = database.InTransaction(client =>
        {
            ConfigurationDraft.CheckVersion(client, expectedVersion);
            var draft = Read(client, id) ?? throw new DraftWriteException("connection.notFound");
            var deviceDrafts = client.Queryable<DeviceDraftRecord>().ToList().Select(DeviceDraftStore.Decode).ToArray();
            var inputDrafts = client.Queryable<InputDraftRecord>().ToList().Select(InputDraftStore.Decode).ToArray();
            var devices = deviceDrafts.Where(device => device.ConnectionId == id || inputDrafts.Any(input =>
                input.DeviceId == device.Id && input.Module?.ConnectionIds.Contains(id) == true))
                .SelectMany(device => new[] { new ConnectionDeviceReference { Id = device.Id, Key = device.Key, Name = device.Name } }
                    .Concat(inputDrafts.Where(input => input.DeviceId == device.Id && input.Module?.ConnectionIds.Contains(id) == true)
                        .Select(input => new ConnectionDeviceReference { Id = device.Id, Key = device.Key, Name = device.Name, InputKey = input.Key })))
                .ToArray();
            var routes = client.Ado.SqlQuery<ConnectionRouteReference>("""
                SELECT DISTINCT r.id AS Id, r.business_key AS Key, r.name AS Name, r.enabled AS Enabled
                FROM route_draft r JOIN route_draft_reference ref ON ref.route_id = r.id
                WHERE ref.kind = 'connection' AND ref.target_id = @id ORDER BY r.id
                """, new SugarParameter("@id", id)).ToArray();
            var state = client.Queryable<SystemStateRecord>().InSingle(SystemStateRecord.SingletonId);
            var pointers = new[] { (Position: "effective", Revision: state.EffectiveRevision),
                (Position: "previous", Revision: state.PreviousRevision), (Position: "pending", Revision: state.PendingRevision) }
                .Where(value => value.Revision is not null).ToArray();
            var snapshots = pointers.Select(value => value.Revision!.Value).Distinct().Select(revision =>
                client.Queryable<RuntimeSnapshotRecord>().InSingle(revision)
                    ?? throw new RuntimePlanDocumentException("运行版本引用缺失。")).ToArray();
            return (draft, devices, routes, pointers, snapshots);
        });
        var versions = captured.snapshots.Select(snapshot =>
        {
            var plan = RuntimeSnapshotDocument.ReadPlan(snapshot);
            var units = plan.Units.Where(unit => RuntimeConnectionReferences.ReadIds(unit).Contains(id))
                .Select(unit => unit.Id.Kind + "/" + unit.Id.Key).Distinct().Order(StringComparer.Ordinal).ToArray();
            return new ConnectionVersionReference(snapshot.Revision,
                captured.pointers.Where(pointer => pointer.Revision == snapshot.Revision).Select(pointer => pointer.Position).ToArray(), units);
        }).Where(reference => reference.Units.Length > 0).ToArray();
        var counts = deliveries.GetTargetStateCounts(id);
        return new(expectedVersion, id, captured.draft.Key, captured.draft.Name, captured.devices, captured.routes, versions,
            counts.Pending, counts.Failed, counts.Quarantined, false,
            captured.devices.Length == 0 && captured.routes.Length == 0 && versions.Length == 0 && counts.Pending == 0 && counts.Failed == 0 && counts.Quarantined == 0);
    }

    public ConnectionDraftResult Write(long? existingId, ConnectionDraftRequest request, AuditAccount account, string? sourceIp)
    {
        var id = existingId ?? ids.NextId();
        var draft = ConnectionDraftValidation.Normalize(id, request);
        if (draft.Module is { } module)
        { _ = modules.Freeze(id, module.Kind, module.Version, module.Parameters); }
        var content = JsonSerializer.Serialize(draft, Json);
        return database.InTransaction(client =>
        {
            ConfigurationDraft.CheckVersion(client, request.ExpectedVersion);
            if (existingId is null && (client.Queryable<MqttCredentialRecord>().InSingle(id) is not null ||
                client.Queryable<HttpCredentialRecord>().InSingle(id) is not null || client.Queryable<ModuleCredentialRecord>().InSingle(id) is not null || client.Queryable<ConnectionTrustRecord>().InSingle(id) is not null || HasRetainedReference(client, id)))
            { throw new DraftWriteException("connection.reservedIdentity"); }
            var existing = existingId is null ? null : client.Queryable<ConnectionDraftRecord>().InSingle(id)
                ?? throw new DraftWriteException("connection.notFound");
            if (existing is not null)
            {
                var previous = Decode(existing);
                if (previous.Key != draft.Key || !previous.SameKind(draft))
                { throw new DraftWriteException("connection.immutableIdentity"); }
            }
            if (client.Queryable<ConnectionDraftRecord>().Any(row => row.BusinessKey == draft.Key && row.Id != id))
            { throw new DraftWriteException("connection.duplicateKey"); }
            DeviceDraftStore.ValidateConnectionChange(client, draft);
            var row = new ConnectionDraftRecord { Id = id, BusinessKey = draft.Key, Content = content };
            if (existing is null)
            { client.Insertable(row).ExecuteCommand(); }
            else
            { client.Updateable(row).ExecuteCommand(); }
            ConfigurationEditJournal.Record(client, "connection", id, account.Name, time);
            var version = ConfigurationDraft.Advance(client, time);
            audit.Append(client, new AuditEntry(existing is null ? "connection.draftCreated" : "connection.draftUpdated",
                AuditResults.Success, account, sourceIp, id.ToString(CultureInfo.InvariantCulture),
                JsonSerializer.Serialize(new { key = draft.Key, previousVersion = request.ExpectedVersion, version })));
            return new ConnectionDraftResult(version, draft);
        });
    }

    public DraftVersion Delete(long id, string expectedVersion, AuditAccount account, string? sourceIp) => database.InTransaction(client =>
    {
        ConfigurationDraft.CheckVersion(client, expectedVersion);
        var row = client.Queryable<ConnectionDraftRecord>().InSingle(id) ?? throw new DraftWriteException("connection.notFound");
        if (client.Queryable<DeviceDraftRecord>().Any(device => device.ConnectionId == id))
        { throw new DraftWriteException("connection.deviceReferenced"); }
        if (InputDraftStore.ReferencesConnection(client, id))
        { throw new DraftWriteException("connection.deviceReferenced"); }
        if (RouteDraftStore.HasReference(client, "connection", id))
        { throw new DraftWriteException("connection.routeReferenced"); }
        // 种子仍可存在，不允许删掉被保留运行版本使用的编号。
        if (HasRetainedReference(client, id))
        { throw new DraftWriteException("connection.referenced"); }
        if (deliveries.ReadPending(id, 0, 1).Count != 0 || deliveries.ReadFailed(id, 0, 1).Count != 0 || deliveries.ReadQuarantined(id, 0, 1).Count != 0)
        { throw new DraftWriteException("connection.hasDeliveries"); }
        var mqttCleared = client.Deleteable<MqttCredentialRecord>().Where(record => record.ConnectionId == id).ExecuteCommand();
        var httpCleared = client.Deleteable<HttpCredentialRecord>().Where(record => record.ConnectionId == id).ExecuteCommand();
        var moduleCleared = client.Deleteable<ModuleCredentialRecord>().Where(record => record.ConnectionId == id).ExecuteCommand();
        var trustCleared = client.Deleteable<ConnectionTrustRecord>().Where(record => record.ConnectionId == id).ExecuteCommand();
        if (mqttCleared != 0 || httpCleared != 0 || moduleCleared != 0 || trustCleared != 0)
        {
            audit.Append(client, new AuditEntry("credential.connectionDeleted", AuditResults.Success, account, sourceIp,
                id.ToString(CultureInfo.InvariantCulture), "删除未被引用且无发送记录的连接草稿，同时清除其认证凭证与连接信任。"));
        }
        client.Deleteable(row).ExecuteCommand();
        ConfigurationCredentialRequirements.Remove(client, id);
        ConfigurationEditJournal.Record(client, "connection", id, account.Name, time, deleted: true);
        var version = ConfigurationDraft.Advance(client, time);
        audit.Append(client, new AuditEntry("connection.draftDeleted", AuditResults.Success, account, sourceIp,
            id.ToString(CultureInfo.InvariantCulture), JsonSerializer.Serialize(new { previousVersion = expectedVersion, version })));
        return new DraftVersion(version);
    });

    internal static bool HasRetainedReference(SqlSugarClient client, long id)
    {
        var state = client.Queryable<SystemStateRecord>().InSingle(SystemStateRecord.SingletonId);
        foreach (var revision in new[] { state.EffectiveRevision, state.PreviousRevision, state.PendingRevision }.OfType<long>().Distinct())
        {
            var snapshot = client.Queryable<RuntimeSnapshotRecord>().InSingle(revision)
                ?? throw new InvalidOperationException("运行版本引用缺失。");
            var plan = RuntimeSnapshotDocument.ReadExecutionPlan(snapshot);
            if (plan.Units.Any(unit => RuntimeConnectionReferences.ReadIds(unit).Contains(id)))
            { return true; }
        }
        return false;
    }

    internal static ConnectionDraft? Read(SqlSugarClient client, long id) =>
        client.Queryable<ConnectionDraftRecord>().InSingle(id) is { } row ? Decode(row) : null;

    internal static ConnectionDraft Decode(ConnectionDraftRecord row)
    {
        var draft = JsonSerializer.Deserialize<ConnectionDraft>(row.Content, Json) ?? throw new JsonException("连接草稿为空。");
        if (draft.Id != row.Id || draft.Key != row.BusinessKey)
        { throw new JsonException("连接草稿标识不一致。"); }
        return ConnectionDraftValidation.Normalize(draft.Id, new("read", draft.Key, draft.Name, draft.Kind,
            draft.Enabled, draft.Modbus, draft.Mqtt, draft.Http, draft.Module));
    }
}

internal sealed record ConnectionDeletionReview(string Version, long Id, string Key, string Name,
    ConnectionDeviceReference[] Devices, ConnectionRouteReference[] Routes, ConnectionVersionReference[] Versions,
    long PendingCount, long FailedCount, long QuarantinedCount, bool MoreDeliveries, bool CanDelete);
internal sealed class ConnectionDeviceReference
{
    public long Id { get; set; }
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string? InputKey { get; set; }
}
internal sealed class ConnectionRouteReference
{
    public long Id { get; set; }
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Enabled { get; set; }
}
internal sealed record ConnectionVersionReference(long Revision, string[] Positions, string[] Units);

[SugarTable("connection_draft")]
internal sealed class ConnectionDraftRecord
{
    [SugarColumn(ColumnName = "id", IsPrimaryKey = true)]
    public long Id { get; set; }
    [SugarColumn(ColumnName = "business_key")]
    public string BusinessKey { get; set; } = string.Empty;
    [SugarColumn(ColumnName = "content")]
    public string Content { get; set; } = string.Empty;
}
