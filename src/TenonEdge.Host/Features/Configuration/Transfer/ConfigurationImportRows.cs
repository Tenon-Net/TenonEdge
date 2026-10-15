using System.Text;
using System.Text.Json;
using SqlSugar;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Devices;
using TenonEdge.Host.Features.Inputs;
using TenonEdge.Host.Features.Routes;
using TenonEdge.Host.Features.Runtime;

namespace TenonEdge.Host.Features.Configuration.Transfer;

/// <summary>导入的预序列化行，不复用回滚的编辑日志恢复语义；保留全部旧日志，由调用方只标记本次变化。</summary>
internal sealed class ConfigurationImportRows(ConfigurationCheckpoint draft)
{
    private readonly ConnectionDraftRecord[] _connections = draft.Connections.Select(value => new ConnectionDraftRecord
    { Id = value.Id, BusinessKey = value.Key, Content = JsonSerializer.Serialize(value, ConfigurationExport.Json) }).ToArray();
    private readonly DeviceDraftRecord[] _devices = draft.Devices.Select(value => new DeviceDraftRecord
    { Id = value.Id, BusinessKey = value.Key, ConnectionId = value.ConnectionId, Content = JsonSerializer.Serialize(value, ConfigurationExport.Json) }).ToArray();
    private readonly InputDraftRecord[] _inputs = draft.Inputs.Select(value => new InputDraftRecord
    { Id = value.Id, BusinessKey = value.Key, DeviceId = value.DeviceId, Content = JsonSerializer.Serialize(value, ConfigurationExport.Json) }).ToArray();
    private readonly RouteDraftRecord[] _routes = draft.Routes.Select(value => new RouteDraftRecord
    {
        Id = value.Id,
        BusinessKey = value.Key,
        Name = value.Name,
        Description = value.Description,
        Enabled = value.Enabled,
        NodeCount = value.Graph.Nodes.Length,
        UpdatedBy = value.UpdatedBy,
        UpdatedAt = value.UpdatedAt,
        Graph = JsonSerializer.Serialize(value.Graph, RouteGraphFormat.Json),
    }).ToArray();
    private readonly RouteDraftReferenceRecord[] _references = draft.Routes.SelectMany(route => RouteGraphFormat.References(route.Graph)
        .Select(reference => new RouteDraftReferenceRecord { RouteId = route.Id, Kind = reference.Kind, TargetId = reference.TargetId })).ToArray();
    private readonly ConfigurationCredentialRequirement[] _required = draft.RequiredCredentials ?? [];

    public void CheckSize(ConfigurationEdit[]? edits, int changedObjects)
    {
        long bytes = _connections.Sum(value => (long)Size(value.Content)) + _devices.Sum(value => (long)Size(value.Content)) +
            _inputs.Sum(value => (long)Size(value.Content)) + _routes.Sum(value => (long)Size(value.Graph) + Size(value.Name) + Size(value.Description)) +
            _required.Sum(value => (long)Size(value.AccountIdentity) + Size(value.Reason) + 64) +
            (edits ?? []).Sum(value => (long)Size(value.UpdatedBy) + 192) + changedObjects * 512L;
        if (bytes > RuntimePlanDocument.MaximumBytes)
        { throw ConfigurationImportDocument.Error("import.draftTooLarge", "$"); }
    }

    public void Write(SqlSugarClient client, HashSet<long> changedRoutes, string? account, long now)
    {
        foreach (var route in _routes.Where(value => changedRoutes.Contains(value.Id)))
        { route.UpdatedBy = account; route.UpdatedAt = now; }
        // Removed identities must never be reused, even when the corresponding
        // object never reached a runtime version. Existing credentials stay intact.
        client.Ado.ExecuteCommand("""
            UPDATE system_state SET id_floor = MAX(id_floor, (
                SELECT COALESCE(MAX(id), 0) FROM (
                    SELECT id FROM connection_draft UNION ALL SELECT id FROM device_draft
                    UNION ALL SELECT id FROM input_draft UNION ALL SELECT id FROM route_draft
                )
            )) WHERE id = 1;
            DELETE FROM route_draft_reference;
            DELETE FROM route_draft;
            DELETE FROM input_draft;
            DELETE FROM device_draft;
            DELETE FROM connection_draft;
            """);
        Insert(client, _connections);
        Insert(client, _devices);
        Insert(client, _inputs);
        Insert(client, _routes);
        Insert(client, _references);
        ConfigurationCredentialRequirements.Replace(client, _required);
    }

    private static int Size(string? value) => value is null ? 0 : Encoding.UTF8.GetByteCount(value);
    private static void Insert<T>(SqlSugarClient client, T[] rows) where T : class, new()
    {
        foreach (var chunk in rows.Chunk(100))
        { client.Insertable(chunk).ExecuteCommand(); }
    }
}
