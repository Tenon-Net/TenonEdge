using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SqlSugar;
using TenonEdge.Host.Features.Audit;
using TenonEdge.Host.Features.Configuration;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Inputs;
using TenonEdge.Host.Features.Routes;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Storage;
using TenonEdge.Host.Storage.Ids;

namespace TenonEdge.Host.Features.Devices;

/// <summary>设备基础配置草稿；引用校验、版本、实体与审计共用短事务，无运行时副作用。</summary>
internal sealed class DeviceDraftStore(ConfigDatabase database, SnowflakeIdGenerator ids, AuditTrail audit, TimeProvider time)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public DeviceDraftList Read() => database.InTransaction(client => new DeviceDraftList(
        ConfigurationDraft.ReadVersion(client),
        client.Queryable<DeviceDraftRecord>().OrderBy(row => row.Id).ToList().Select(Decode).ToArray()));

    public DeviceDraftResult Write(long? existingId, DeviceDraftRequest request, string? account, string? sourceIp)
    {
        var id = existingId ?? ids.NextId();
        return database.InTransaction(client =>
        {
            ConfigurationDraft.CheckVersion(client, request.ExpectedVersion);
            var connection = request.ConnectionId is { } connectionId ? ConnectionDraftStore.Read(client, connectionId) : null;
            var device = DeviceDraftValidation.Normalize(id, request, connection);
            var existing = existingId is null ? null : client.Queryable<DeviceDraftRecord>().InSingle(id)
                ?? throw new DraftWriteException("device.notFound");
            if (existing is not null && existing.BusinessKey != device.Key)
            { throw new DraftWriteException("device.immutableKey"); }
            if (client.Queryable<DeviceDraftRecord>().Any(row => row.BusinessKey == device.Key && row.Id != id))
            { throw new DraftWriteException("device.duplicateKey"); }
            if (existing is null && HasRetainedReference(client, device.Key))
            { throw new DraftWriteException("device.reservedKey"); }
            InputDraftStore.ValidateDeviceChange(client, device);
            var row = new DeviceDraftRecord
            {
                Id = id,
                BusinessKey = device.Key,
                ConnectionId = device.ConnectionId,
                Content = JsonSerializer.Serialize(device, Json),
            };
            if (existing is null)
            { client.Insertable(row).ExecuteCommand(); }
            else
            { client.Updateable(row).ExecuteCommand(); }
            var version = ConfigurationDraft.Advance(client, time);
            audit.Append(client, new AuditEntry(existing is null ? "device.draftCreated" : "device.draftUpdated",
                AuditResults.Success, account, sourceIp, id.ToString(CultureInfo.InvariantCulture),
                JsonSerializer.Serialize(new { key = device.Key, previousVersion = request.ExpectedVersion, version })));
            return new DeviceDraftResult(version, device);
        });
    }

    public DeviceDraftCopyResult Copy(long sourceId, DeviceDraftCopyRequest request, string? account, string? sourceIp)
    {
        if (request.Count is < 1 or > 20 || string.IsNullOrEmpty(request.ExpectedVersion) || request.ExpectedVersion.Length > 64)
        { throw new ArgumentException("复制数量须为 1—20，且须提供草稿版本。"); }
        return database.InTransaction(client =>
        {
            ConfigurationDraft.CheckVersion(client, request.ExpectedVersion);
            var source = Read(client, sourceId) ?? throw new DraftWriteException("device.notFound");
            var inputs = InputDraftStore.ReadForDevice(client, sourceId);
            var copies = new List<DeviceDraft>(request.Count);
            for (var index = 0; index < request.Count; index++)
            {
                long id;
                string key;
                do
                {
                    id = ids.NextId();
                    key = CopyKey(source.Key, id);
                } while (client.Queryable<DeviceDraftRecord>().Any(row => row.BusinessKey == key) || HasRetainedReference(client, key));
                // The connection is shared, not cloned. Source/point enablement is preserved;
                // disabling the copied device prevents accidental duplicate acquisition.
                var copy = source with { Id = id, Key = key, Enabled = false };
                client.Insertable(new DeviceDraftRecord
                {
                    Id = id,
                    BusinessKey = key,
                    ConnectionId = copy.ConnectionId,
                    Content = JsonSerializer.Serialize(copy, Json),
                }).ExecuteCommand();
                InputDraftStore.CopyToDevice(client, inputs, id, ids);
                copies.Add(copy);
            }
            var version = ConfigurationDraft.Advance(client, time);
            audit.Append(client, new AuditEntry("device.draftCopied", AuditResults.Success, account, sourceIp,
                sourceId.ToString(CultureInfo.InvariantCulture),
                JsonSerializer.Serialize(new { copies = copies.Select(copy => new { copy.Id, copy.Key }), previousVersion = request.ExpectedVersion, version })));
            return new DeviceDraftCopyResult(version, copies.ToArray());
        });
    }

    internal static string CopyKey(string sourceKey, long id)
    {
        var suffix = "_copy_" + id.ToString(CultureInfo.InvariantCulture);
        return sourceKey[..Math.Min(sourceKey.Length, 64 - suffix.Length)] + suffix;
    }

    public DraftVersion Delete(long id, string expectedVersion, string? account, string? sourceIp) => database.InTransaction(client =>
    {
        ConfigurationDraft.CheckVersion(client, expectedVersion);
        var row = client.Queryable<DeviceDraftRecord>().InSingle(id) ?? throw new DraftWriteException("device.notFound");
        if (RouteDraftStore.HasDeviceReference(client, id))
        { throw new DraftWriteException("device.routeReferenced"); }
        // 平台映射持久化后须在这里补入同事务引用检查。
        if (HasRetainedReference(client, row.BusinessKey))
        { throw new DraftWriteException("device.referenced"); }
        client.Deleteable<InputDraftRecord>().Where(input => input.DeviceId == id).ExecuteCommand();
        client.Deleteable(row).ExecuteCommand();
        var version = ConfigurationDraft.Advance(client, time);
        audit.Append(client, new AuditEntry("device.draftDeleted", AuditResults.Success, account, sourceIp,
            id.ToString(CultureInfo.InvariantCulture), JsonSerializer.Serialize(new { previousVersion = expectedVersion, version })));
        return new DraftVersion(version);
    });

    internal static void ValidateConnectionChange(SqlSugarClient client, ConnectionDraft connection)
    {
        foreach (var device in client.Queryable<DeviceDraftRecord>().Where(row => row.ConnectionId == connection.Id).ToList().Select(Decode))
        {
            try
            {
                DeviceDraftValidation.Normalize(device.Id, new("check", device.Key, device.Name, device.Kind, device.Group,
                    device.ConnectionId, device.UnitId, device.AddressNotation, device.Enabled), connection);
                InputDraftStore.ValidateDeviceChange(client, device, connection);
            }
            catch (ArgumentException) { throw new DraftWriteException("connection.deviceConflict"); }
        }
    }

    private static bool HasRetainedReference(SqlSugarClient client, string key)
    {
        var state = client.Queryable<SystemStateRecord>().InSingle(SystemStateRecord.SingletonId);
        foreach (var revision in new[] { state.EffectiveRevision, state.PreviousRevision, state.PendingRevision }.OfType<long>().Distinct())
        {
            var snapshot = client.Queryable<RuntimeSnapshotRecord>().InSingle(revision)
                ?? throw new InvalidOperationException("运行版本引用缺失。");
            var plan = RuntimePlanDocument.Parse(Encoding.UTF8.GetBytes(snapshot.Content));
            if (plan.Units.Any(unit =>
                unit.Id.Kind.StartsWith("input.", StringComparison.Ordinal) && unit.Id.KeySegments[0] == key ||
                unit.Configuration.TryGetProperty("inputs", out var inputs) &&
                    inputs.EnumerateArray().Any(input => input.GetString()!.Split('/')[0] == key)))
            { return true; }
        }
        return false;
    }

    internal static DeviceDraft? Read(SqlSugarClient client, long id) =>
        client.Queryable<DeviceDraftRecord>().InSingle(id) is { } row ? Decode(row) : null;

    private static DeviceDraft Decode(DeviceDraftRecord row)
    {
        var device = JsonSerializer.Deserialize<DeviceDraft>(row.Content, Json) ?? throw new JsonException("设备草稿为空。");
        if (device.Id != row.Id || device.Key != row.BusinessKey || device.ConnectionId != row.ConnectionId)
        { throw new JsonException("设备草稿标识或连接引用不一致。"); }
        return device;
    }
}

[SugarTable("device_draft")]
internal sealed class DeviceDraftRecord
{
    [SugarColumn(ColumnName = "id", IsPrimaryKey = true)]
    public long Id { get; set; }
    [SugarColumn(ColumnName = "business_key")]
    public string BusinessKey { get; set; } = string.Empty;
    [SugarColumn(ColumnName = "connection_id", IsNullable = true)]
    public long? ConnectionId { get; set; }
    [SugarColumn(ColumnName = "content")]
    public string Content { get; set; } = string.Empty;
}
