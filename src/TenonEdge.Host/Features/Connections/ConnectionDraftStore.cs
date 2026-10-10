using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using SqlSugar;
using TenonEdge.Host.Features.Audit;
using TenonEdge.Host.Features.Configuration;
using TenonEdge.Host.Features.Credentials;
using TenonEdge.Host.Features.Devices;
using TenonEdge.Host.Features.Routes;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Storage;
using TenonEdge.Host.Storage.Ids;
using TenonEdge.Runtime.Deliveries;

namespace TenonEdge.Host.Features.Connections;

/// <summary>非秘密连接草稿。变更、全局版本及审计在同一个短事务中提交；没有运行时或网络副作用。</summary>
internal sealed class ConnectionDraftStore(ConfigDatabase database, SnowflakeIdGenerator ids, AuditTrail audit,
    TimeProvider time, DeliveryCache deliveries)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public ConnectionDraftList Read() => database.InTransaction(client => new ConnectionDraftList(
        ConfigurationDraft.ReadVersion(client), client.Queryable<ConnectionDraftRecord>().OrderBy(row => row.Id).ToList().Select(Decode).ToArray()));

    public ConnectionDraftResult Write(long? existingId, ConnectionDraftRequest request, string? account, string? sourceIp)
    {
        var id = existingId ?? ids.NextId();
        var draft = ConnectionDraftValidation.Normalize(id, request);
        var content = JsonSerializer.Serialize(draft, Json);
        return database.InTransaction(client =>
        {
            ConfigurationDraft.CheckVersion(client, request.ExpectedVersion);
            if (existingId is null && (client.Queryable<MqttCredentialRecord>().InSingle(id) is not null ||
                client.Queryable<HttpCredentialRecord>().InSingle(id) is not null || HasRetainedReference(client, id)))
            { throw new DraftWriteException("connection.reservedIdentity"); }
            var existing = existingId is null ? null : client.Queryable<ConnectionDraftRecord>().InSingle(id)
                ?? throw new DraftWriteException("connection.notFound");
            if (existing is not null)
            {
                var previous = Decode(existing);
                if (previous.Key != draft.Key || previous.Kind != draft.Kind)
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
            ConfigurationEditJournal.Record(client, "connection", id, account, time);
            var version = ConfigurationDraft.Advance(client, time);
            audit.Append(client, new AuditEntry(existing is null ? "connection.draftCreated" : "connection.draftUpdated",
                AuditResults.Success, account, sourceIp, id.ToString(CultureInfo.InvariantCulture),
                JsonSerializer.Serialize(new { key = draft.Key, previousVersion = request.ExpectedVersion, version })));
            return new ConnectionDraftResult(version, draft);
        });
    }

    public DraftVersion Delete(long id, string expectedVersion, string? account, string? sourceIp) => database.InTransaction(client =>
    {
        ConfigurationDraft.CheckVersion(client, expectedVersion);
        var row = client.Queryable<ConnectionDraftRecord>().InSingle(id) ?? throw new DraftWriteException("connection.notFound");
        if (client.Queryable<DeviceDraftRecord>().Any(device => device.ConnectionId == id))
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
        if (mqttCleared != 0 || httpCleared != 0)
        {
            audit.Append(client, new AuditEntry("credential.connectionDeleted", AuditResults.Success, account, sourceIp,
                id.ToString(CultureInfo.InvariantCulture), "删除未被引用且无发送记录的连接草稿，同时清除其认证凭证。"));
        }
        client.Deleteable(row).ExecuteCommand();
        ConfigurationEditJournal.Record(client, "connection", id, account, time, deleted: true);
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
            var plan = RuntimeSnapshotDocument.ReadPlan(snapshot);
            if (plan.Units.Any(unit => unit.Configuration.TryGetProperty("connectionId", out var value) && value.Deserialize<long>(JsonSerializerOptions.Web) == id))
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
            draft.Enabled, draft.Modbus, draft.Mqtt, draft.Http));
    }
}

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
