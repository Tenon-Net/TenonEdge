using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using SqlSugar;
using TenonEdge.Host.Features.Audit;
using TenonEdge.Host.Features.Configuration;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Devices;
using TenonEdge.Host.Features.Routes;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Storage;
using TenonEdge.Host.Storage.Ids;
using TenonEdge.Runtime.Inputs;

namespace TenonEdge.Host.Features.Inputs;

internal sealed partial class InputDraftStore(ConfigDatabase database, SnowflakeIdGenerator ids, AuditTrail audit, TimeProvider time, InputModuleCatalog modules)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public InputDraftList Read(long deviceId) => database.InTransaction(client =>
    {
        _ = DeviceDraftStore.Read(client, deviceId) ?? throw new DraftWriteException("device.notFound");
        return new InputDraftList(ConfigurationDraft.ReadVersion(client), ReadForDevice(client, deviceId));
    });

    public InputDraftResult Write(long deviceId, long? existingId, InputDraftRequest request, AuditAccount account, string? sourceIp)
    {
        var id = existingId ?? ids.NextId();
        var snapshot = database.InTransaction(client =>
        {
            ConfigurationDraft.CheckVersion(client, request.ExpectedVersion);
            var device = DeviceDraftStore.Read(client, deviceId) ?? throw new DraftWriteException("device.notFound");
            // Reject invalid resource ownership before invoking any extension code.
            if (existingId is not null)
            {
                var existing = client.Queryable<InputDraftRecord>().InSingle(id);
                if (existing is null || existing.DeviceId != deviceId)
                { throw new DraftWriteException("input.notFound"); }
            }
            _ = request.Module?.Normalize(device, request.Key); // Pure shape/size guard before reading referenced rows.
            var siblings = ReadForDevice(client, deviceId).Where(other => other.Id != id).ToArray();
            var references = siblings.SelectMany(other => other.Module?.ConnectionIds ?? [])
                .Concat(request.Module?.ConnectionIds ?? []).Distinct().ToArray();
            var connections = references.Length == 0 ? [] : client.Queryable<ConnectionDraftRecord>()
                .Where(row => references.Contains(row.Id)).ToList().Select(ConnectionDraftStore.Decode).ToArray();
            return (Device: device, Connections: connections.ToDictionary(value => value.Id),
                Connection: device.ConnectionId is { } connectionId ? ConnectionDraftStore.Read(client, connectionId) : null,
                Siblings: siblings);
        });
        // Trusted module Describe is pure, but never runs under a configuration DB
        // transaction. Recheck the captured version before publishing its result.
        var input = InputDraftValidation.Normalize(id, snapshot.Device, request, snapshot.Connection);
        var keys = snapshot.Siblings.SelectMany(other => InputDraftValidation.FieldKeys(other, modules, snapshot.Connections)).ToHashSet(StringComparer.Ordinal);
        if (InputDraftValidation.FieldKeys(input, modules, snapshot.Connections).Any(keys.Contains))
        { throw new DraftWriteException("input.duplicateDeviceField"); }
        return database.InTransaction(client =>
        {
            ConfigurationDraft.CheckVersion(client, request.ExpectedVersion);
            var device = snapshot.Device;
            var existing = existingId is null ? null : client.Queryable<InputDraftRecord>().InSingle(id)
                ?? throw new DraftWriteException("input.notFound");
            if (existing is not null && existing.DeviceId != deviceId)
            { throw new DraftWriteException("input.notFound"); }
            if (existing is not null && existing.BusinessKey != input.Key)
            { throw new DraftWriteException("input.immutableKey"); }
            var siblings = snapshot.Siblings;
            if (siblings.Any(other => other.Key == input.Key))
            { throw new DraftWriteException("input.duplicateKey"); }
            if (existing is null && HasRetainedReference(client, device.Key + "/" + input.Key))
            { throw new DraftWriteException("input.reservedKey"); }
            var row = new InputDraftRecord { Id = id, DeviceId = deviceId, BusinessKey = input.Key, Content = JsonSerializer.Serialize(input, Json) };
            if (existing is null)
            { client.Insertable(row).ExecuteCommand(); }
            else
            { client.Updateable(row).ExecuteCommand(); }
            ConfigurationEditJournal.Record(client, "input", id, account.Name, time);
            var version = ConfigurationDraft.Advance(client, time);
            audit.Append(client, new AuditEntry(existing is null ? "input.draftCreated" : "input.draftUpdated",
                AuditResults.Success, account, sourceIp, id.ToString(CultureInfo.InvariantCulture),
                JsonSerializer.Serialize(new { deviceId, key = input.Key, previousVersion = request.ExpectedVersion, version })));
            return new InputDraftResult(version, input);
        });
    }

    public DraftVersion Delete(long deviceId, long id, string expectedVersion, AuditAccount account, string? sourceIp) => database.InTransaction(client =>
    {
        ConfigurationDraft.CheckVersion(client, expectedVersion);
        var device = DeviceDraftStore.Read(client, deviceId) ?? throw new DraftWriteException("device.notFound");
        var row = client.Queryable<InputDraftRecord>().InSingle(id);
        if (row is null || row.DeviceId != deviceId)
        { throw new DraftWriteException("input.notFound"); }
        if (HasRetainedReference(client, device.Key + "/" + row.BusinessKey))
        { throw new DraftWriteException("input.referenced"); }
        if (RouteDraftStore.HasReference(client, "input", id))
        { throw new DraftWriteException("input.routeReferenced"); }
        client.Deleteable(row).ExecuteCommand();
        ConfigurationEditJournal.Record(client, "input", id, account.Name, time, deleted: true);
        var version = ConfigurationDraft.Advance(client, time);
        audit.Append(client, new AuditEntry("input.draftDeleted", AuditResults.Success, account, sourceIp,
            id.ToString(CultureInfo.InvariantCulture), JsonSerializer.Serialize(new { deviceId, previousVersion = expectedVersion, version })));
        return new DraftVersion(version);
    });

    internal static InputDraft[] ReadForDevice(SqlSugarClient client, long deviceId) =>
        client.Queryable<InputDraftRecord>().Where(row => row.DeviceId == deviceId).OrderBy(row => row.Id).ToList().Select(Decode).ToArray();

    internal static bool ReferencesConnection(SqlSugarClient client, long id) => client.Queryable<InputDraftRecord>().ToList()
        .Select(Decode).Any(input => input.Module?.ConnectionIds.Contains(id) == true);

    // Caller owns the device-copy transaction, version and single batch audit.
    internal static void CopyToDevice(SqlSugarClient client, InputDraft[] sources, long deviceId, SnowflakeIdGenerator ids, AuditAccount account, TimeProvider time)
    {
        foreach (var source in sources)
        {
            var input = source with { Id = ids.NextId(), DeviceId = deviceId };
            client.Insertable(new InputDraftRecord
            {
                Id = input.Id,
                DeviceId = deviceId,
                BusinessKey = input.Key,
                Content = JsonSerializer.Serialize(input, Json),
            }).ExecuteCommand();
            ConfigurationEditJournal.Record(client, "input", input.Id, account.Name, time);
        }
    }

    internal static void ValidateDeviceChange(SqlSugarClient client, DeviceDraft device, ConnectionDraft? replacement = null)
    {
        foreach (var input in ReadForDevice(client, device.Id))
        {
            if (input.Kind != device.Kind)
            { throw new DraftWriteException("device.sourceConflict"); }
            var connection = replacement ?? (device.ConnectionId is { } id ? ConnectionDraftStore.Read(client, id) : null);
            try
            {
                _ = InputDraftValidation.Normalize(input.Id, device, new("check", input.Key, input.Name, input.Enabled,
                    input.Modbus, input.Mqtt, input.Http, input.Simulator, input.Module), connection);
            }
            catch (ArgumentException) { throw new DraftWriteException("device.sourceConflict"); }
        }
    }

    private static bool HasRetainedReference(SqlSugarClient client, string key)
    {
        var state = client.Queryable<SystemStateRecord>().InSingle(SystemStateRecord.SingletonId);
        foreach (var revision in new[] { state.EffectiveRevision, state.PreviousRevision, state.PendingRevision }.OfType<long>().Distinct())
        {
            var snapshot = client.Queryable<RuntimeSnapshotRecord>().InSingle(revision)
                ?? throw new InvalidOperationException("运行版本引用缺失。");
            var plan = RuntimeSnapshotDocument.ReadPlan(snapshot);
            if (plan.Units.Any(unit =>
                unit.Id.Kind.StartsWith("input.", StringComparison.Ordinal) && unit.Id.Key == key ||
                unit.Configuration.TryGetProperty("inputs", out var inputs) && inputs.EnumerateArray().Any(input => input.GetString() == key)))
            { return true; }
        }
        return false;
    }

    internal static InputDraft Decode(InputDraftRecord row)
    {
        var input = JsonSerializer.Deserialize<InputDraft>(row.Content, Json) ?? throw new JsonException("来源草稿为空。");
        if (input.Id != row.Id || input.DeviceId != row.DeviceId || input.Key != row.BusinessKey)
        { throw new JsonException("来源草稿标识不一致。"); }
        return input;
    }
}

[SugarTable("input_draft")]
internal sealed class InputDraftRecord
{
    [SugarColumn(ColumnName = "id", IsPrimaryKey = true)]
    public long Id { get; set; }
    [SugarColumn(ColumnName = "device_id")]
    public long DeviceId { get; set; }
    [SugarColumn(ColumnName = "business_key")]
    public string BusinessKey { get; set; } = string.Empty;
    [SugarColumn(ColumnName = "content")]
    public string Content { get; set; } = string.Empty;
}
