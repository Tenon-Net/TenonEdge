using System.Collections.Immutable;
using System.Text.Json;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Runtime.Mapping;
using TenonEdge.Runtime.Transforms;

namespace TenonEdge.Host.Features.Routes;

internal sealed record RouteFieldPage(string Version, string NodeId, bool Analyzed, MappedFieldSchema[] Fields,
    string? NextAfterKey, int CommonCount, int UpstreamCount, bool UnitsDiffer, string[] ExcludedFields, bool DifferencesTruncated, RouteTopologyIssue[] Issues);

internal static class RouteFieldAnalysis
{
    private const string Key = "field-analysis";

    // Restrict inspection to the selected node's ancestors, never fabricate executable output settings.
    internal static RouteGraph Prefix(RouteGraph graph, string nodeId, TransformCatalog? transforms = null)
    {
        graph = RouteGraphFormat.Normalize(graph, transforms);
        var selected = graph.Nodes.SingleOrDefault(node => node.Id == nodeId) ?? throw Error("node.missing", nodeId);
        var wanted = new HashSet<string>(StringComparer.Ordinal) { nodeId };
        var pending = new Stack<string>();
        pending.Push(nodeId);
        while (pending.TryPop(out var id))
        {
            foreach (var edge in graph.Edges.Where(edge => edge.Target == id))
            { if (wanted.Add(edge.Source)) { pending.Push(edge.Source); } }
        }
        var nodes = graph.Nodes.Where(node => wanted.Contains(node.Id)).ToArray();
        var edges = graph.Edges.Where(edge => wanted.Contains(edge.Source) && wanted.Contains(edge.Target)).ToArray();
        if (selected.Type.StartsWith("input.", StringComparison.Ordinal))
        {
            if (edges.Any(edge => edge.Target == nodeId))
            { throw Error("edge.direction", nodeId); }
            var sink = nodeId == "field-sink" ? "field-sink-2" : "field-sink";
            nodes = [selected, new(sink, "output.http", 1, "Field sink", JsonSerializer.SerializeToElement(new { connectionId = (long?)null }))];
            edges = [new(nodeId, sink)];
        }
        else
        {
            // Such an edge is a cycle through the selected node; stripping it would hide invalid ancestry.
            if (edges.Any(edge => edge.Source == nodeId))
            { throw Error("graph.cycle", nodeId); }
            nodes = nodes.Select(node => node.Id == nodeId
                ? node with { Type = "output.http", Version = 1, Config = JsonSerializer.SerializeToElement(new { connectionId = (long?)null }) }
                : node).ToArray();
        }
        return new(1, nodes, edges, new([]));
    }

    internal static RouteFieldPage Analyze(RouteCompilationSnapshot snapshot, string nodeId, string? afterKey, string? search, int limit, CancellationToken token)
    {
        if (limit is < 1 or > 100 || afterKey is { Length: > 64 } || search is { Length: > 64 })
        { throw new ArgumentException("字段分页参数无效。"); }
        var graph = snapshot.Routes.Single().Graph;
        var sources = new Dictionary<long, RouteTopologySource>();
        var topologyNodes = new List<RouteTopologyNode>();
        foreach (var node in graph.Nodes)
        {
            token.ThrowIfCancellationRequested();
            var ids = node.Type.StartsWith("input.", StringComparison.Ordinal)
                ? node.Config.Deserialize<RouteInputConfig>(RouteGraphFormat.Json)!.SourceIds.ToImmutableArray() : [];
            topologyNodes.Add(new(node.Id, node.Type, node.Version, ids));
            foreach (var id in ids)
            {
                if (sources.ContainsKey(id))
                { continue; }
                if (!snapshot.Inputs.TryGetValue(id, out var input) || !snapshot.Devices.TryGetValue(input.DeviceId, out var device))
                { throw Error("source.missing", node.Id, id); }
                var connection = device.ConnectionId is { } connectionId ? snapshot.Connections.GetValueOrDefault(connectionId) : null;
                try
                { sources.Add(id, RouteSourceCompiler.Compile(input, device, connection, false).Source); }
                catch (Exception error) when (error is ArgumentException or JsonException or InvalidOperationException or FormatException)
                { throw Error("source.configuration", node.Id, id); }
            }
        }
        var topology = RouteTopologyCompiler.Compile([new(Key, true, 1, topologyNodes.ToImmutableArray(), graph.Edges.Select(edge => new RouteTopologyEdge(edge.Source, edge.Target)).ToImmutableArray())], sources, token, snapshot.Admission, snapshot.Modules);
        var schemas = new Dictionary<string, ImmutableArray<MappedFieldSchema>>(StringComparer.Ordinal);
        var propagation = new RouteFieldPropagation(graph.Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal), snapshot.Modules);
        foreach (var path in topology.Paths)
        {
            var (upstream, fields) = propagation.Propagate(path, sources[path.SourceId].Fields, token);
            schemas.TryAdd(upstream, fields);
        }
        var common = schemas.Values.First().ToDictionary(field => field.Key, StringComparer.Ordinal);
        var baseline = common.Keys.ToHashSet(StringComparer.Ordinal);
        var excluded = new HashSet<string>(StringComparer.Ordinal);
        var truncated = false;
        void Exclude(string key)
        {
            if (excluded.Contains(key))
            { return; }
            if (excluded.Count < 100)
            { excluded.Add(key); }
            else
            { truncated = true; }
        }
        var unitsDiffer = false;
        foreach (var fields in schemas.Values.Skip(1))
        {
            token.ThrowIfCancellationRequested();
            var available = fields.ToDictionary(field => field.Key, StringComparer.Ordinal);
            foreach (var key in available.Keys)
            { if (!baseline.Contains(key)) { Exclude(key); } }
            foreach (var key in common.Keys.ToArray())
            {
                if (!available.TryGetValue(key, out var other) || common[key].Type != other.Type)
                { common.Remove(key); Exclude(key); }
                else if (common[key].Unit != other.Unit)
                { unitsDiffer = true; common[key] = common[key] with { Unit = null }; }
            }
        }
        var page = common.Values.Where(field => (afterKey is null || string.CompareOrdinal(field.Key, afterKey) > 0) &&
            (string.IsNullOrEmpty(search) || field.Key.Contains(search, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(field => field.Key, StringComparer.Ordinal).Take(limit + 1).ToArray();
        return new(snapshot.Version, nodeId, true, page.Take(limit).ToArray(), page.Length > limit ? page[limit - 1].Key : null,
            common.Count, schemas.Count, unitsDiffer, excluded.Order(StringComparer.Ordinal).ToArray(), truncated, []);
    }
    private static RouteTopologyException Error(string code, string node, long? source = null) => new(new(code, Key, node, source));
}
