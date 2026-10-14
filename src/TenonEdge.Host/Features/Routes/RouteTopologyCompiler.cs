using System.Collections.Immutable;
using TenonEdge.Abstractions.Messages;

namespace TenonEdge.Host.Features.Routes;

/// <summary>§7.2 的有界拓扑编译：逐图校验并逆拓扑饱和计数、跨路线累计，全部通过后才展开。
/// 只产出有序节点链，不声称完成映射、输出配置、引用实体或运行计划编译。</summary>
internal static class RouteTopologyCompiler
{
    internal const int MaximumNodes = 50;
    internal const int MaximumSourcesPerNode = 500;
    internal const int MaximumSources = 2000;
    internal const int MaximumPathsPerSource = 32;
    internal const int MaximumPaths = 20000;
    private static readonly HashSet<string> Kinds = new(StringComparer.Ordinal)
    { "input.modbus", "input.mqtt", "input.http", "input.simulator", "transform.map", "output.mqtt", "output.http", "output.tenon" };

    internal static RouteTopologyResult Compile(IReadOnlyList<RouteTopology> routes,
        IReadOnlyDictionary<long, RouteTopologySource> sources, CancellationToken cancellationToken, RouteCompilationLimits? limits = null) =>
        CompileCore(routes, sources, limits, checkFields: true, cancellationToken);

    // Only for diagnostic labels of an already frozen execution plan. Retain every
    // identity, graph and expansion bound, without revalidating historical fields.
    internal static RouteTopologyResult RecoverDiagnosticPaths(IReadOnlyList<RouteTopology> routes,
        IReadOnlyDictionary<long, RouteTopologySource> sources) =>
        CompileCore(routes, sources, null, checkFields: false, CancellationToken.None);

    private static RouteTopologyResult CompileCore(IReadOnlyList<RouteTopology> routes,
        IReadOnlyDictionary<long, RouteTopologySource> sources, RouteCompilationLimits? limits, bool checkFields, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentNullException.ThrowIfNull(sources);
        cancellationToken.ThrowIfCancellationRequested();
        limits ??= RouteCompilationLimits.Default;
        limits.Validate();
        var active = new List<RouteTopology>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var route in routes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (route is null)
            { Reject("route.invalid", ""); }
            if (!route!.Enabled)
            { continue; }
            if (!BusinessKeys.IsValid(route.Key) || !keys.Add(route.Key))
            { Reject("route.key", route.Key); }
            active.Add(route);
            // 每条有效启用路线至少产生一条路径，此处是全局路径上限的必要条件，不另加路线限制。
            if (active.Count > limits.TotalPaths)
            { Reject("paths.totalLimit", route.Key); }
        }
        active.Sort((left, right) => string.CompareOrdinal(left.Key, right.Key));
        var graphs = new List<Graph>();
        var schemas = new Dictionary<long, Dictionary<string, FieldType>>();
        var identities = new Dictionary<string, long>(StringComparer.Ordinal);
        var counts = new Dictionary<long, int>();
        var total = 0;
        foreach (var route in active)
        {
            var graph = Validate(route, sources, schemas, identities, limits, checkFields, cancellationToken);
            graphs.Add(graph);
            var paths = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var index = graph.Order.Count - 1; index >= 0; index--)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var id = graph.Order[index];
                var count = graph.Nodes[id].Kind.StartsWith("output.", StringComparison.Ordinal) ? 1 : 0;
                foreach (var child in graph.Next[id])
                { count = Math.Min(limits.PathsPerSource + 1, count + paths[child]); }
                paths[id] = count;
            }
            foreach (var input in graph.Inputs)
            {
                foreach (var source in input.SourceIds)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var count = Math.Min(limits.PathsPerSource + 1, counts.GetValueOrDefault(source) + paths[input.Id]);
                    if (count > limits.PathsPerSource)
                    { Reject("paths.sourceLimit", graph.Route.Key, input.Id, source); }
                    counts[source] = count;
                    total += paths[input.Id];
                    if (total > limits.TotalPaths)
                    { Reject("paths.totalLimit", graph.Route.Key, input.Id, source); }
                }
            }
        }
        // 所有图和全局预算均通过之前，不分配任何展开路径。
        var expanded = ImmutableArray.CreateBuilder<RouteTopologyPath>(total);
        foreach (var graph in graphs)
        {
            foreach (var input in graph.Inputs)
            {
                foreach (var source in input.SourceIds.OrderBy(id => sources[id].UnitId.ToString(), StringComparer.Ordinal))
                {
                    var chain = new List<string>(MaximumNodes);
                    Expand(input.Id);
                    void Expand(string id)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        chain.Add(id);
                        if (graph.Next[id].Count == 0)
                        { expanded.Add(new(graph.Route.Key, source, chain.ToImmutableArray())); }
                        else
                        { foreach (var next in graph.Next[id]) { Expand(next); } }
                        chain.RemoveAt(chain.Count - 1);
                    }
                }
            }
        }
        return new(expanded.MoveToImmutable(), counts.ToImmutableDictionary());
    }

    private static Graph Validate(RouteTopology route, IReadOnlyDictionary<long, RouteTopologySource> sources,
        Dictionary<long, Dictionary<string, FieldType>> schemas, Dictionary<string, long> identities, RouteCompilationLimits limits, bool checkFields, CancellationToken token)
    {
        if (route.SchemaVersion != 1)
        { Reject("route.version", route.Key); }
        if (route.Nodes.IsDefaultOrEmpty || route.Nodes.Length > MaximumNodes)
        { Reject("nodes.limit", route.Key); }
        if (route.Edges.IsDefault || route.Edges.Length > MaximumNodes * (MaximumNodes - 1))
        { Reject("edges.limit", route.Key); }
        var nodes = new Dictionary<string, RouteTopologyNode>(StringComparer.Ordinal);
        var next = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var degrees = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var node in route.Nodes)
        {
            token.ThrowIfCancellationRequested();
            if (node is null || !BusinessKeys.IsValid(node.Id))
            { Reject("node.id", route.Key); }
            if (!nodes.TryAdd(node!.Id, node))
            { Reject("node.duplicate", route.Key, node.Id); }
            if (!Kinds.Contains(node.Kind))
            { Reject("node.kind", route.Key, node.Id); }
            if (node.Version != 1)
            { Reject("node.version", route.Key, node.Id); }
            next[node.Id] = [];
            degrees[node.Id] = 0;
            if (!node.Kind.StartsWith("input.", StringComparison.Ordinal))
            {
                if (!node.SourceIds.IsDefaultOrEmpty)
                { Reject("source.unexpected", route.Key, node.Id); }
                continue;
            }
            if (node.SourceIds.IsDefaultOrEmpty || node.SourceIds.Length > MaximumSourcesPerNode)
            { Reject("sources.nodeLimit", route.Key, node.Id); }
            var ids = new HashSet<long>();
            Dictionary<string, FieldType>? baseline = null;
            foreach (var id in node.SourceIds)
            {
                token.ThrowIfCancellationRequested();
                if (!ids.Add(id))
                { Reject("source.duplicate", route.Key, node.Id, id); }
                if (!sources.TryGetValue(id, out var source) || source is null || source.Id != id || id is < 1 or > 9007199254740991L)
                { Reject("source.missing", route.Key, node.Id, id); }
                if (!source!.Enabled)
                { Reject("source.disabled", route.Key, node.Id, id); }
                if (source.UnitId is null || source.UnitId.Kind != node.Kind || source.UnitId.KeySegments.Count != 2)
                { Reject("source.kind", route.Key, node.Id, id); }
                if (!schemas.TryGetValue(id, out var fields))
                {
                    if (schemas.Count == limits.Sources)
                    { Reject("sources.totalLimit", route.Key, node.Id, id); }
                    if (!identities.TryAdd(source.UnitId.Key, id))
                    { Reject("source.identityDuplicate", route.Key, node.Id, id); }
                    if (checkFields && (source.Fields.IsDefaultOrEmpty || source.Fields.Length > (source.UnitId.Kind == "input.modbus" ? 10000 : 1000)))
                    { Reject("source.fields", route.Key, node.Id, id); }
                    fields = new Dictionary<string, FieldType>(StringComparer.Ordinal);
                    foreach (var field in checkFields ? source.Fields : [])
                    {
                        if (field is null || !BusinessKeys.IsValid(field.Key) || !Enum.IsDefined(field.Type) || !fields.TryAdd(field.Key, field.Type))
                        { Reject("source.fields", route.Key, node.Id, id); }
                    }
                    schemas.Add(id, fields);
                }
                if (baseline is not null && (fields.Count != baseline.Count || baseline.Any(field => !fields.TryGetValue(field.Key, out var type) || type != field.Value)))
                {
                    var differing = baseline.Keys.Concat(fields.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
                        .First(key => !baseline.TryGetValue(key, out var left) || !fields.TryGetValue(key, out var right) || left != right);
                    Reject("source.schemaMismatch", route.Key, node.Id, id, field: differing);
                }
                baseline ??= fields;
            }
        }
        var pairs = new HashSet<(string Source, string Target)>();
        for (var index = 0; index < route.Edges.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            var edge = route.Edges[index];
            if (edge is null || edge.Source is null || edge.Target is null || !nodes.ContainsKey(edge.Source) || !nodes.ContainsKey(edge.Target))
            { Reject("edge.missing", route.Key, edgeIndex: index); }
            if (!pairs.Add((edge!.Source, edge.Target)))
            { Reject("edge.duplicate", route.Key, edgeIndex: index); }
            if (nodes[edge.Source].Kind.StartsWith("output.", StringComparison.Ordinal) || nodes[edge.Target].Kind.StartsWith("input.", StringComparison.Ordinal))
            { Reject("edge.direction", route.Key, edgeIndex: index); }
            next[edge.Source].Add(edge.Target);
            degrees[edge.Target]++;
        }
        foreach (var node in nodes.Values)
        {
            if ((!node.Kind.StartsWith("input.", StringComparison.Ordinal) && degrees[node.Id] == 0) ||
                (!node.Kind.StartsWith("output.", StringComparison.Ordinal) && next[node.Id].Count == 0))
            { Reject("node.dangling", route.Key, node.Id); }
            next[node.Id].Sort(StringComparer.Ordinal);
        }
        var ready = new Queue<string>(degrees.Where(pair => pair.Value == 0).Select(pair => pair.Key).Order(StringComparer.Ordinal));
        var order = new List<string>(nodes.Count);
        while (ready.TryDequeue(out var id))
        {
            token.ThrowIfCancellationRequested();
            order.Add(id);
            foreach (var child in next[id])
            { if (--degrees[child] == 0) { ready.Enqueue(child); } }
        }
        if (order.Count != nodes.Count)
        {
            var colors = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var id in nodes.Keys.Order(StringComparer.Ordinal))
            { if (Cycle(id) is { } cycle) { Reject("graph.cycle", route.Key, cycle); } }
            string? Cycle(string id)
            {
                var color = colors.GetValueOrDefault(id);
                if (color != 0)
                { return color == 1 ? id : null; }
                colors[id] = 1;
                foreach (var child in next[id])
                { if (Cycle(child) is { } found) { return found; } }
                colors[id] = 2;
                return null;
            }
        }
        return new(route, nodes, next, order, nodes.Values.Where(node => node.Kind.StartsWith("input.", StringComparison.Ordinal)).OrderBy(node => node.Id, StringComparer.Ordinal).ToArray());
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Reject(string code, string route, string? node = null, long? source = null, int? edgeIndex = null, string? field = null) =>
        throw new RouteTopologyException(new(code, route, node, source, edgeIndex, field));

    private sealed record Graph(RouteTopology Route, Dictionary<string, RouteTopologyNode> Nodes, Dictionary<string, List<string>> Next,
        List<string> Order, RouteTopologyNode[] Inputs);
}
