using System.Text.Json;
using System.Text.Json.Serialization;
using SqlSugar;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Devices;
using TenonEdge.Host.Features.Inputs;
using TenonEdge.Host.Features.Routes;

namespace TenonEdge.Host.Features.Configuration;

/// <summary>事务外准备完整草稿行；提交事务内只重检版本、替换配置及索引，不改凭证。</summary>
internal sealed class ConfigurationReplacement(ConfigurationCheckpoint draft)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) } };

    private readonly ConfigurationEdit[]? _edits = draft.Edits;
    private readonly ConnectionDraftRecord[] _connections = draft.Connections.Select(value => new ConnectionDraftRecord
    { Id = value.Id, BusinessKey = value.Key, Content = JsonSerializer.Serialize(value, Json) }).ToArray();
    private readonly DeviceDraftRecord[] _devices = draft.Devices.Select(value => new DeviceDraftRecord
    { Id = value.Id, BusinessKey = value.Key, ConnectionId = value.ConnectionId, Content = JsonSerializer.Serialize(value, Json) }).ToArray();
    private readonly InputDraftRecord[] _inputs = draft.Inputs.Select(value => new InputDraftRecord
    { Id = value.Id, BusinessKey = value.Key, DeviceId = value.DeviceId, Content = JsonSerializer.Serialize(value, Json) }).ToArray();
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

    public bool Apply(SqlSugarClient client, string expectedVersion, TimeProvider time)
    {
        if (ConfigurationDraft.ReadVersion(client) != expectedVersion)
        { return false; }
        client.Ado.ExecuteCommand("""
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
        ConfigurationEditJournal.Restore(client, _edits);
        ConfigurationDraft.Advance(client, time);
        return true;
    }

    private static void Insert<T>(SqlSugarClient client, T[] rows) where T : class, new()
    {
        foreach (var chunk in rows.Chunk(100))
        { client.Insertable(chunk).ExecuteCommand(); }
    }
}
