using System.Globalization;
using System.Text.Json;
using SqlSugar;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Host.Features.Audit;
using TenonEdge.Host.Features.Configuration;
using TenonEdge.Host.Storage;
using TenonEdge.Host.Storage.Ids;

namespace TenonEdge.Host.Features.Routes;

/// <summary>图草稿、引用索引、全局版本与审计原子保存；不持有运行时，也不执行图。</summary>
internal sealed partial class RouteDraftStore(ConfigDatabase database, SnowflakeIdGenerator ids, AuditTrail audit, TimeProvider time)
{
    public RouteDraftList Read() => database.InTransaction(client => new RouteDraftList(ConfigurationDraft.ReadVersion(client),
        client.Queryable<RouteDraftRecord>().OrderBy(row => row.Id).ToList().Select(row =>
            new RouteDraftSummary(row.Id, row.BusinessKey, row.Name, row.Description, row.Enabled, row.NodeCount, row.UpdatedBy, row.UpdatedAt)).ToArray()));

    public RouteDraftResult Read(long id) => database.InTransaction(client => new RouteDraftResult(ConfigurationDraft.ReadVersion(client), ReadRoute(client, id)));

    public RouteDraftResult Write(long? existingId, RouteDraftRequest request, string? account, string? sourceIp)
    {
        Validate(request.ExpectedVersion, request.Key, request.Name, request.Description);
        var graph = RouteGraphFormat.Normalize(request.Graph);
        var id = existingId ?? ids.NextId();
        return database.InTransaction(client =>
        {
            ConfigurationDraft.CheckVersion(client, request.ExpectedVersion);
            if (existingId is not null && ReadRoute(client, id).Key != request.Key)
            { throw new DraftWriteException("route.immutableKey"); }
            Unique(client, request.Key, id);
            var route = new RouteDraft(id, request.Key, request.Name.Trim(), request.Description?.Trim(), request.Enabled, graph, account, time.GetUtcNow().ToUnixTimeMilliseconds());
            Save(client, route, existingId is not null);
            return Finish(client, route, existingId is null ? "route.draftCreated" : "route.draftUpdated", request.ExpectedVersion, account, sourceIp);
        });
    }

    public RouteDraftResult Copy(long sourceId, RouteDraftCopyRequest request, string? account, string? sourceIp)
    {
        Validate(request.ExpectedVersion, request.Key, request.Name, null);
        var id = ids.NextId();
        return database.InTransaction(client =>
        {
            ConfigurationDraft.CheckVersion(client, request.ExpectedVersion);
            var source = ReadRoute(client, sourceId);
            Unique(client, request.Key, id);
            var copy = source with { Id = id, Key = request.Key, Name = request.Name.Trim(), Enabled = false, UpdatedBy = account, UpdatedAt = time.GetUtcNow().ToUnixTimeMilliseconds() };
            Save(client, copy, false);
            return Finish(client, copy, "route.draftCopied", request.ExpectedVersion, account, sourceIp, sourceId);
        });
    }

    public DraftVersion Delete(long id, string expectedVersion, string? account, string? sourceIp) => database.InTransaction(client =>
    {
        ConfigurationDraft.CheckVersion(client, expectedVersion);
        _ = ReadRoute(client, id);
        // 索引外键随图草稿删除；保留运行版本不改，现存投递不撤回。
        client.Deleteable<RouteDraftRecord>().In(id).ExecuteCommand();
        ConfigurationEditJournal.Record(client, "route", id, account, time, deleted: true);
        var version = ConfigurationDraft.Advance(client, time);
        audit.Append(client, new AuditEntry("route.draftDeleted", AuditResults.Success, account, sourceIp, id.ToString(CultureInfo.InvariantCulture),
            JsonSerializer.Serialize(new { previousVersion = expectedVersion, version })));
        return new DraftVersion(version);
    });

    internal static bool HasReference(SqlSugarClient client, string kind, long id) =>
        client.Queryable<RouteDraftReferenceRecord>().Any(row => row.Kind == kind && row.TargetId == id);

    internal static bool HasDeviceReference(SqlSugarClient client, long deviceId) => client.Ado.GetInt(
        "SELECT COUNT(*) FROM route_draft_reference r JOIN input_draft i ON r.target_id = i.id WHERE r.kind = 'input' AND i.device_id = @id",
        new SugarParameter("@id", deviceId)) > 0;

    private RouteDraftResult Finish(SqlSugarClient client, RouteDraft route, string action, string previousVersion, string? account, string? sourceIp, long? copiedFrom = null)
    {
        ConfigurationEditJournal.Record(client, "route", route.Id, account, time);
        var version = ConfigurationDraft.Advance(client, time);
        audit.Append(client, new AuditEntry(action, AuditResults.Success, account, sourceIp, route.Id.ToString(CultureInfo.InvariantCulture),
            JsonSerializer.Serialize(new { key = route.Key, previousVersion, version, copiedFrom })));
        return new(version, route);
    }

    private static void Save(SqlSugarClient client, RouteDraft route, bool existing)
    {
        var row = new RouteDraftRecord
        {
            Id = route.Id,
            BusinessKey = route.Key,
            Name = route.Name,
            Description = route.Description,
            Enabled = route.Enabled,
            NodeCount = route.Graph.Nodes.Length,
            UpdatedBy = route.UpdatedBy,
            UpdatedAt = route.UpdatedAt,
            Graph = JsonSerializer.Serialize(route.Graph, RouteGraphFormat.Json),
        };
        if (existing)
        { client.Updateable(row).ExecuteCommand(); }
        else
        { client.Insertable(row).ExecuteCommand(); }
        client.Deleteable<RouteDraftReferenceRecord>().Where(reference => reference.RouteId == route.Id).ExecuteCommand();
        var references = RouteGraphFormat.References(route.Graph).Select(reference => new RouteDraftReferenceRecord
        { RouteId = route.Id, Kind = reference.Kind, TargetId = reference.TargetId }).ToArray();
        if (references.Length > 0)
        { client.Insertable(references).ExecuteCommand(); }
    }

    private static RouteDraft ReadRoute(SqlSugarClient client, long id)
    {
        var row = client.Queryable<RouteDraftRecord>().InSingle(id) ?? throw new DraftWriteException("route.notFound");
        return Decode(row);
    }

    internal static RouteDraft Decode(RouteDraftRecord row) => new(row.Id, row.BusinessKey, row.Name, row.Description, row.Enabled,
            JsonSerializer.Deserialize<RouteGraph>(row.Graph, RouteGraphFormat.Json) ?? throw new InvalidOperationException("路线草稿损坏。"), row.UpdatedBy, row.UpdatedAt);

    private static void Unique(SqlSugarClient client, string key, long id)
    {
        if (client.Queryable<RouteDraftRecord>().Any(row => row.BusinessKey == key && row.Id != id))
        { throw new DraftWriteException("route.duplicateKey"); }
    }
    private static void Validate(string version, string key, string name, string? description)
    {
        if (string.IsNullOrEmpty(version) || version.Length > 64 || !BusinessKeys.IsValid(key) || string.IsNullOrWhiteSpace(name) ||
            name.Length > 64 || name.Any(char.IsControl) || description is { Length: > 1000 })
        { throw new ArgumentException("路线名称、key、说明或草稿版本无效。"); }
    }
}

[SugarTable("route_draft")]
internal sealed class RouteDraftRecord
{
    [SugarColumn(ColumnName = "id", IsPrimaryKey = true)] public long Id { get; set; }
    [SugarColumn(ColumnName = "business_key")] public string BusinessKey { get; set; } = "";
    [SugarColumn(ColumnName = "name")] public string Name { get; set; } = "";
    [SugarColumn(ColumnName = "description", IsNullable = true)] public string? Description { get; set; }
    [SugarColumn(ColumnName = "enabled")] public bool Enabled { get; set; }
    [SugarColumn(ColumnName = "node_count")] public int NodeCount { get; set; }
    [SugarColumn(ColumnName = "updated_by", IsNullable = true)] public string? UpdatedBy { get; set; }
    [SugarColumn(ColumnName = "updated_at")] public long UpdatedAt { get; set; }
    [SugarColumn(ColumnName = "graph")] public string Graph { get; set; } = "";
}
[SugarTable("route_draft_reference")]
internal sealed class RouteDraftReferenceRecord
{
    [SugarColumn(ColumnName = "route_id", IsPrimaryKey = true)] public long RouteId { get; set; }
    [SugarColumn(ColumnName = "kind", IsPrimaryKey = true)] public string Kind { get; set; } = "";
    [SugarColumn(ColumnName = "target_id", IsPrimaryKey = true)] public long TargetId { get; set; }
}
