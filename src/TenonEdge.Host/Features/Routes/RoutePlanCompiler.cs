using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Abstractions.Plans;
using TenonEdge.Adapters.Http;
using TenonEdge.Adapters.Mqtt;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Features.Devices;
using TenonEdge.Host.Features.Inputs;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Runtime.Inputs;
using TenonEdge.Runtime.Mapping;
using TenonEdge.Runtime.Outputs;
using TenonEdge.Runtime.Transforms;

namespace TenonEdge.Host.Features.Routes;

internal sealed record RouteCompilationSnapshot(string Version, RouteDraft[] Routes, IReadOnlyDictionary<long, DeviceDraft> Devices,
    IReadOnlyDictionary<long, InputDraft> Inputs, IReadOnlyDictionary<long, ConnectionDraft> Connections, IReadOnlySet<long> MqttCredentials, RouteCompilationLimits? Limits = null, TransformCatalog? Transforms = null, InputModuleCatalog? InputModules = null, OutputModuleCatalog? OutputModules = null)
{
    public OutputModuleCatalog OutputsCatalog => OutputModules ?? OutputModuleCatalog.Empty;
    public InputModuleCatalog InputsCatalog => InputModules ?? InputModuleCatalog.Empty;
    public TransformCatalog Modules => Transforms ?? TransformCatalog.BuiltIn;
    public RouteCompilationLimits Admission => Limits ?? RouteCompilationLimits.Default;
}
internal sealed record RouteCompiledPath(RouteTopologyPath Path, RuntimeUnitId OutputId, ImmutableArray<MappedFieldSchema> Fields);
internal sealed record RoutePlanCompilation(RuntimePlanDocument Document, RouteTopologyResult Topology, ImmutableArray<RouteCompiledPath> Paths);

/// <summary>纯本地编译：来源只生成一次，逐路径传播字段和映射，最终复用运行快照静态校验。没有运行时、数据库或网络依赖。</summary>
internal static partial class RoutePlanCompiler
{
    public static RoutePlanCompilation Compile(RouteCompilationSnapshot snapshot, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var limits = snapshot.Admission;
        limits.Validate();
        var routes = snapshot.Routes.Where(route => route.Enabled).OrderBy(route => route.Key, StringComparer.Ordinal).ToArray();
        var graphs = new List<RouteTopology>();
        var normalizedGraphs = new Dictionary<string, RouteGraph>(StringComparer.Ordinal);
        var sources = new Dictionary<long, CompiledRouteSource>();
        var units = new List<RuntimeUnitPlan>();
        long bytes = 32;
        foreach (var route in routes)
        {
            token.ThrowIfCancellationRequested();
            RouteGraph graph;
            try
            { graph = RouteGraphFormat.Normalize(route.Graph, snapshot.Modules, snapshot.InputsCatalog, snapshot.OutputsCatalog); }
            catch (Exception error) when (ConfigurationError(error)) { throw Error("graph.format", route.Key); }
            if (!BusinessKeys.IsValid(route.Key) || !normalizedGraphs.TryAdd(route.Key, graph))
            { throw Error("route.key", route.Key); }
            var nodes = new List<RouteTopologyNode>();
            foreach (var node in graph.Nodes)
            {
                var ids = node.Type.StartsWith("input.", StringComparison.Ordinal)
                    ? node.Config.Deserialize<RouteInputConfig>(RouteGraphFormat.Json)!.SourceIds.ToImmutableArray() : [];
                nodes.Add(new(node.Id, node.Type, node.Version, ids));
                foreach (var id in ids)
                {
                    token.ThrowIfCancellationRequested();
                    if (sources.ContainsKey(id))
                    { continue; }
                    if (sources.Count == limits.Sources)
                    { throw Error("sources.totalLimit", route.Key, node.Id, id); }
                    if (!snapshot.Inputs.TryGetValue(id, out var input))
                    { throw Error("source.missing", route.Key, node.Id, id); }
                    if (!snapshot.Devices.TryGetValue(input.DeviceId, out var device))
                    { throw Error("source.deviceMissing", route.Key, node.Id, id); }
                    var connection = device.ConnectionId is { } connectionId ? snapshot.Connections.GetValueOrDefault(connectionId) : null;
                    if (!input.Enabled || !device.Enabled || connection is { Enabled: false })
                    { throw Error("source.disabled", route.Key, node.Id, id); }
                    CompiledRouteSource source;
                    try
                    { source = RouteSourceCompiler.Compile(input, device, connection, connection is not null && snapshot.MqttCredentials.Contains(connection.Id), snapshot.InputsCatalog, snapshot.Connections, snapshot.MqttCredentials); }
                    catch (Exception error) when (ConfigurationError(error)) { throw Error("source.configuration", route.Key, node.Id, id); }
                    sources.Add(id, source);
                    Add(source.Unit, route.Key, node.Id);
                }
            }
            graphs.Add(new(route.Key, true, graph.SchemaVersion, nodes.ToImmutableArray(), graph.Edges.Select(edge => new RouteTopologyEdge(edge.Source, edge.Target)).ToImmutableArray()));
        }
        var topology = RouteTopologyCompiler.Compile(graphs, sources.ToDictionary(pair => pair.Key, pair => pair.Value.Source), token, limits, snapshot.Modules, snapshot.InputsCatalog, snapshot.OutputsCatalog);
        var compiled = ImmutableArray.CreateBuilder<RouteCompiledPath>(topology.Paths.Length);
        var pathsByRoute = topology.Paths.ToLookup(path => path.RouteKey, StringComparer.Ordinal);
        foreach (var route in routes)
        {
            var nodes = normalizedGraphs[route.Key].Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
            var propagation = new RouteFieldPropagation(nodes, snapshot.Modules);
            var checkedSchemas = new HashSet<(string Node, string Upstream)>();
            // 同一图节点链的不同来源共用目标定义，但每条来源消息仍独立投递，不做 join。
            foreach (var group in pathsByRoute[route.Key].GroupBy(path => string.Join('/', path.Nodes), StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested();
                var chain = group.First().Nodes;
                var output = nodes[chain[^1]];
                var outputId = OutputPathId(output.Type, route.Key, chain);
                var middle = chain.Skip(1).SkipLast(1).Select(id => nodes[id]).ToArray();
                var extended = middle.Any(node => node.Type != "transform.map" || node.Version != 1);
                var stages = middle.Select(node => extended ? JsonSerializer.SerializeToElement(new { kind = node.Type, version = node.Version, configuration = node.Config }) : node.Config.GetProperty("rules")).ToArray();
                var outputSchemas = new List<(long SourceId, ImmutableArray<MappedFieldSchema> Fields)>();
                foreach (var path in group)
                {
                    var (upstream, fields) = propagation.Propagate(path, sources[path.SourceId].Source.Fields, token);
                    if (checkedSchemas.Add((output.Id, upstream)))
                    { ValidateOutputFields(output, fields, route.Key, path.SourceId); }
                    outputSchemas.Add((path.SourceId, fields));
                    compiled.Add(new(path, outputId, fields));
                }
                try
                {
                    var configuration = Output(output, group.Select(path => sources[path.SourceId].Source.UnitId.Key).Order(StringComparer.Ordinal).ToArray(), stages, snapshot, route.Key, extended);
                    if (OutputModuleCatalog.IsModuleKind(output.Type))
                    {
                        var protocol = RuntimeConnectionReferences.Protocol(new(outputId, configuration));
                        foreach (var (sourceId, fields) in outputSchemas)
                        { ValidateOutputFields(output with { Config = protocol.Configuration }, fields, route.Key, sourceId); }
                    }
                    Add(new(outputId, configuration), route.Key, output.Id);
                }
                catch (Exception error) when (ConfigurationError(error)) { throw Error("output.configuration", route.Key, output.Id); }
            }
        }
        foreach (var queue in QueueOutputs(snapshot.Connections.Values, units, id => snapshot.MqttCredentials.Contains(id), token))
        { Add(queue, "", queue.Id.Key); }
        token.ThrowIfCancellationRequested();
        var serialized = JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, units = units.Select(unit => new { kind = unit.Id.Kind, key = unit.Id.Key, unit.Configuration }) }, RouteSourceCompiler.Json);
        if (serialized.LongLength > RuntimePlanDocument.MaximumBytes)
        { throw Error("plan.size", ""); }
        RuntimePlanDocument document;
        try
        { document = RuntimePlanDocument.Parse(serialized, snapshot.Modules, snapshot.InputsCatalog, snapshot.OutputsCatalog); }
        catch (RuntimePlanDocumentException) { throw Error("plan.configuration", ""); }
        limits.Check(document);
        var paths = compiled.MoveToImmutable();
        document = document.WithDiagnosticObjects(paths.GroupBy(path => path.OutputId).ToDictionary(group => group.Key,
            group => RuntimeUnitId.Create(group.Key.Kind, group.First().Path.RouteKey + "/" + group.First().Path.Nodes[^1])));
        return new(document, topology, paths);

        void Add(RuntimeUnitPlan unit, string route, string node)
        {
            // 展开过程中逐单元检查；不先构造整个超限运行快照。
            bytes += Encoding.UTF8.GetByteCount(unit.Configuration.GetRawText()) + Encoding.UTF8.GetByteCount(unit.Id.Key) + unit.Id.Kind.Length + 64;
            if (bytes > RuntimePlanDocument.MaximumBytes)
            { throw Error("plan.size", route, node); }
            units.Add(unit);
        }
    }

    internal static JsonElement Output(RouteGraphNode node, string[] inputs, JsonElement[] stages, RouteCompilationSnapshot snapshot, string route, bool extended = false)
    {
        if (node.Type == "output.tenon")
        { throw Error("output.unavailable", route, node.Id); }
        var id = node.Config.TryGetProperty("connectionId", out var reference) && reference.ValueKind == JsonValueKind.Number ? reference.GetInt64() : (long?)null;
        if (id is null || !snapshot.Connections.TryGetValue(id.Value, out var connection))
        { throw Error("output.connectionMissing", route, node.Id); }
        if (!connection.Enabled)
        { throw Error("output.connectionDisabled", route, node.Id); }
        if (OutputModuleCatalog.IsModuleKind(node.Type))
        { return node.Config.Deserialize<RouteModuleOutputDraft>(RouteGraphFormat.Json)!.Bind(node, route, inputs, stages, extended, snapshot); }
        var mapping = stages.Length == 0 ? default : JsonSerializer.SerializeToElement(stages);
        if (node.Type == "output.mqtt" && connection.Mqtt is { } mqtt && connection.Kind == ConnectionKind.Mqtt)
        {
            var config = node.Config.Deserialize<RouteMqttConfig>(RouteGraphFormat.Json)!;
            var json = JsonSerializer.SerializeToElement(new MqttOutputSettings
            {
                ConnectionId = connection.Id,
                Host = mqtt.Host,
                Port = mqtt.Port,
                Tls = mqtt.Tls,
                ClientId = mqtt.ClientId,
                TimeoutMs = mqtt.TimeoutMs,
                PingIntervalMs = mqtt.PingIntervalMs,
                UsePassword = snapshot.MqttCredentials.Contains(connection.Id),
                Inputs = inputs,
                Topic = config.Topic ?? "tenonedge/{deviceKey}/data",
                Qos = config.Qos ?? 1,
                Payload = config.Payload,
                MappingStages = extended ? default : mapping,
                TransformStages = extended ? mapping : default,
            }, RouteSourceCompiler.SettingsJson);
            _ = MqttOutputSettings.Parse(json);
            return json;
        }
        if (node.Type == "output.http" && connection.Http is { } http && connection.Kind == ConnectionKind.Http)
        {
            var config = node.Config.Deserialize<RouteHttpConfig>(RouteGraphFormat.Json)!;
            var headers = new Dictionary<string, string>(http.Headers ?? [], StringComparer.OrdinalIgnoreCase);
            foreach (var header in config.Headers ?? [])
            { headers[header.Key] = header.Value; }
            // 原始单节点请求头也单独检查，不能由合并掩盖大小写重复。
            _ = new HttpRequestSettings { ConnectionId = connection.Id, BaseAddress = http.BaseAddress, Headers = (config.Headers ?? []).ToImmutableDictionary() }.ValidateRequest();
            var json = JsonSerializer.SerializeToElement(new HttpOutputSettings
            {
                ConnectionId = connection.Id,
                BaseAddress = http.BaseAddress,
                TimeoutMs = http.TimeoutMs,
                Inputs = inputs.ToImmutableArray(),
                Path = config.Path ?? "",
                Headers = headers.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase),
                SuccessPath = config.SuccessPath,
                SuccessValue = config.SuccessValue,
                Payload = config.Payload,
                MappingStages = extended ? default : mapping,
                TransformStages = extended ? mapping : default,
            }, RouteSourceCompiler.SettingsJson);
            var settings = HttpOutputSettings.Parse(json);
            if (QueryHelpers.ParseQuery(settings.Endpoint.Query).Keys.Any(ConnectionDraftValidation.IsSecretName))
            { throw new ArgumentException("路径不能携带秘密查询参数。"); }
            return json;
        }
        throw Error("output.connectionKind", route, node.Id);
    }

    internal static void ValidateOutputFields(RouteGraphNode output, ImmutableArray<MappedFieldSchema> fields, string route, long source)
    {
        if (!output.Config.TryGetProperty("payload", out var payload))
        { return; }
        try
        {
            _ = OutputPayloadFormat.Parse(payload);
            if (payload.ValueKind == JsonValueKind.Object && payload.Deserialize<RoutePayloadConfig>(RouteGraphFormat.Json)?.Fields is { } selected)
            {
                RequireFields(selected.Select(field => field.SourceField).OfType<string>(), fields, route, output.Id, source);
            }
        }
        catch (Exception error) when (ConfigurationError(error)) { throw Error("output.payload", route, output.Id, source); }
    }

    private static void RequireFields(IEnumerable<string> referenced, ImmutableArray<MappedFieldSchema> fields, string route, string node, long source)
    {
        var known = fields.Select(field => field.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var field in referenced)
        { if (!known.Contains(field)) { throw new RouteTopologyException(new("field.missing", route, node, source, FieldKey: field)); } }
    }
    private static bool ConfigurationError(Exception error) => error is not RouteTopologyException && (error is ArgumentException or JsonException or InvalidOperationException or FormatException or RuntimeUnitBuildException);
    private static RouteTopologyException Error(string code, string route, string? node = null, long? source = null) => new(new(code, route, node, source));
}
