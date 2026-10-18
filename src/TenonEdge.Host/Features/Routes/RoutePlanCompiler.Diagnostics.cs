using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TenonEdge.Abstractions.Plans;
using TenonEdge.Host.Features.Configuration;
using TenonEdge.Host.Features.Runtime;

namespace TenonEdge.Host.Features.Routes;

internal static partial class RoutePlanCompiler
{
    private static RuntimeUnitId OutputPathId(string kind, string route, IEnumerable<string> chain) =>
        RuntimeUnitId.Create(kind, route + "/" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('/', chain)))));

    /// <summary>从已校验哈希的完整历史重建诊断归属，不读取当前草稿/凭证，不重新编译映射、报文或连接。
    /// 只对与冻结计划完全匹配的路径附标签；种子计划仍按原单元标识诊断。历史正文/哈希不变。</summary>
    internal static RuntimePlanDocument WithOutputDiagnosticObjects(RuntimePlanDocument plan, ConfigurationCheckpoint draft)
    {
        try
        {
            if (draft.Devices.Any(device => device is null) || draft.Inputs.Any(input => input is null) || draft.Routes.Any(route => route is null))
            { throw new ArgumentException("历史诊断对象无效。"); }
            var known = plan.Units.Select(unit => unit.Id).ToHashSet();
            var devices = draft.Devices.ToDictionary(device => device.Id);
            var sources = new Dictionary<long, RouteTopologySource>();
            foreach (var input in draft.Inputs)
            {
                if (!devices.TryGetValue(input.DeviceId, out var device))
                { continue; }
                var id = RuntimeUnitId.Create("input." + input.Kind.ToString().ToLowerInvariant(), device.Key + "/" + input.Key);
                if (known.Contains(id))
                {
                    // Field compatibility was already checked when this plan was frozen.
                    // Only identities and topology are needed to recover path labels.
                    sources.Add(input.Id, new(input.Id, id, true, []));
                }
            }
            var graphs = draft.Routes.Where(route => route.Enabled).Select(route =>
            {
                var graph = RouteGraphFormat.NormalizeStored(route.Graph);
                return new RouteTopology(route.Key, true, graph.SchemaVersion,
                    graph.Nodes.Select(node => new RouteTopologyNode(node.Id, node.Type, node.Version,
                        node.Type.StartsWith("input.", StringComparison.Ordinal)
                            ? node.Config.Deserialize<RouteInputConfig>(RouteGraphFormat.Json)!.SourceIds.ToImmutableArray() : [])).ToImmutableArray(),
                    graph.Edges.Select(edge => new RouteTopologyEdge(edge.Source, edge.Target)).ToImmutableArray());
            }).ToArray();
            // Reuse bounded counting before any enumeration; never walk a hostile
            // diamond graph exponentially merely to populate a diagnostic label.
            var topology = RouteTopologyCompiler.RecoverDiagnosticPaths(graphs, sources);
            var kinds = graphs.ToDictionary(graph => graph.Key, graph => graph.Nodes.ToDictionary(node => node.Id, node => node.Kind));
            var objects = new Dictionary<RuntimeUnitId, RuntimeUnitId>();
            foreach (var path in topology.Paths)
            {
                var kind = kinds[path.RouteKey][path.Nodes[^1]];
                var id = OutputPathId(kind, path.RouteKey, path.Nodes);
                if (known.Contains(id))
                { objects[id] = RuntimeUnitId.Create(kind, path.RouteKey + "/" + path.Nodes[^1]); }
            }
            return plan.WithDiagnosticObjects(CompleteConservatively(objects));
        }
        catch (Exception error) when (error is ArgumentException or JsonException or InvalidOperationException)
        {
            // Diagnostic labels must not turn an otherwise executable old plan
            // (e.g. a missing module) into a global startup failure. Unresolved
            // outputs share one conservative bucket per kind, never more budget.
            return plan.WithDiagnosticObjects(CompleteConservatively([]));
        }

        Dictionary<RuntimeUnitId, RuntimeUnitId> CompleteConservatively(Dictionary<RuntimeUnitId, RuntimeUnitId> objects)
        {
            foreach (var unit in plan.Units.Where(unit => unit.Id.Kind.StartsWith("output.", StringComparison.Ordinal)))
            {
                if (unit.Configuration.TryGetProperty("queueOnly", out var queueOnly) && queueOnly.ValueKind == JsonValueKind.True)
                { continue; }
                objects.TryAdd(unit.Id, RuntimeUnitId.Create(unit.Id.Kind, "unresolved"));
            }
            return objects;
        }
    }
}
