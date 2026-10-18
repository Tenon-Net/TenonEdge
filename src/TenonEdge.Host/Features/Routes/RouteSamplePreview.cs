using System.Collections.Immutable;
using System.Text.Json;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Abstractions.Plans;
using TenonEdge.Adapters.Http;
using TenonEdge.Adapters.Mqtt;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Runtime.Mapping;

namespace TenonEdge.Host.Features.Routes;

internal sealed record RoutePreviewField(string Key, string Type, string? Unit, string? Text, string Quality, QualityCode? Reason);
internal sealed record RoutePreviewStage(string NodeId, bool Succeeded, RoutePreviewField[] Fields, FieldMappingIssue[] Issues, bool Truncated);
internal sealed record RoutePreviewOutput(string NodeId, string Kind, Guid DeliveryId, string Address, int? Qos,
    IReadOnlyDictionary<string, string> Headers, string Body, int BodyBytes, bool Truncated, Guid? TestSendId = null);
internal sealed record RoutePreviewPath(ImmutableArray<string> Nodes, bool Succeeded, RoutePreviewStage[] Stages,
    RoutePreviewOutput? Output, RouteTopologyIssue[] Issues);
internal sealed record RouteSamplePreviewResult(string Version, long SourceId, Guid SourceMessageId, DateTimeOffset CollectedAt,
    TimeSource TimeSource, int TotalPaths, int SucceededPaths, bool Truncated, RoutePreviewStage? Source, RoutePreviewPath[] Paths)
{
    public override string ToString() => "Route sample preview (body omitted)";
}

/// <summary>仅执行已取得样本的纯映射/组装。没有运行单元工厂、连接管理、凭证提供者、发送器或队列依赖。</summary>
internal static class RouteSamplePreview
{
    // A caller must separately acquire/authorize a sample. This function never samples a device.
    internal static RouteSamplePreviewResult Run(RouteCompilationSnapshot snapshot, long sourceId, EdgeMessage sample, CancellationToken token, RouteTestSendCapture? capture = null)
    {
        ArgumentNullException.ThrowIfNull(sample);
        token.ThrowIfCancellationRequested();
        if (sample.BudgetBytes > FieldMapping.MaximumResultBytes + RawJsonPayload.MaximumBudgetBytes)
        { throw new InputMessageSizeException("预览输入消息超过解码结果与原始 JSON 的合计预算。"); }
        if (snapshot.Routes.Length != 1)
        { throw new ArgumentException("样本预览只接受当前一张图。"); }
        var route = snapshot.Routes[0];
        var graph = RouteGraphFormat.Normalize(route.Graph, snapshot.Modules, snapshot.InputsCatalog, snapshot.OutputsCatalog);
        var sources = new Dictionary<long, CompiledRouteSource>();
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
                if (sources.Count == snapshot.Admission.Sources)
                { throw Error("sources.totalLimit", node.Id, id); }
                if (!snapshot.Inputs.TryGetValue(id, out var input) || !snapshot.Devices.TryGetValue(input.DeviceId, out var device))
                { throw Error("source.missing", node.Id, id); }
                var connection = device.ConnectionId is { } connectionId ? snapshot.Connections.GetValueOrDefault(connectionId) : null;
                try
                { sources.Add(id, RouteSourceCompiler.Compile(input, device, connection, false, snapshot.InputsCatalog, snapshot.Connections, snapshot.MqttCredentials)); }
                catch (Exception error) when (ConfigurationError(error))
                { throw Error("source.configuration", node.Id, id); }
            }
        }
        if (!sources.TryGetValue(sourceId, out var selected))
        { throw Error("source.missing", null, sourceId); }
        var expected = selected.Source.UnitId.KeySegments;
        if (sample.DeviceKey != expected[0] || sample.InputId != expected[1])
        { throw new ArgumentException("样本身份不属于所选来源。"); }
        // Validate/count the entire graph before expansion; then execute only this sample's source paths.
        var topology = RouteTopologyCompiler.Compile([new(route.Key, true, graph.SchemaVersion, topologyNodes.ToImmutableArray(),
            graph.Edges.Select(edge => new RouteTopologyEdge(edge.Source, edge.Target)).ToImmutableArray())],
            sources.ToDictionary(pair => pair.Key, pair => pair.Value.Source), token, snapshot.Admission, snapshot.Modules, snapshot.InputsCatalog, snapshot.OutputsCatalog);
        var paths = topology.Paths.Where(path => path.SourceId == sourceId).ToArray();
        var nodes = graph.Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        var propagation = new RouteFieldPropagation(nodes, snapshot.Modules);
        var mappings = new Dictionary<string, IMessageTransform>(StringComparer.Ordinal);
        var display = new RoutePreviewDisplay(snapshot.Version, sourceId, sample, paths.Length);
        display.Source(selected.Source.Fields, sample, token);
        foreach (var path in paths)
        {
            token.ThrowIfCancellationRequested();
            var state = display.Path(path.Nodes);
            var message = sample;
            var activeNode = path.Nodes[0];
            var phase = "mapping.configuration";
            try
            {
                // Schema failures remain path-local, just like data failures. Never reuse a failed validation cache entry.
                var (_, fields) = propagation.Propagate(path, selected.Source.Fields, token);
                foreach (var id in path.Nodes.Skip(1).SkipLast(1))
                {
                    token.ThrowIfCancellationRequested();
                    activeNode = id;
                    phase = "mapping.configuration";
                    if (!mappings.TryGetValue(id, out var mapping))
                    {
                        mapping = snapshot.Modules.Build(nodes[id].Type, nodes[id].Version, nodes[id].Config);
                        mappings.Add(id, mapping);
                    }
                    phase = "mapping.data";
                    var mapped = mapping.Apply(message);
                    display.Stage(state, id, mapping.Schema.Select(field => new MappedFieldSchema(field.Key, field.Type, field.Unit)).ToImmutableArray(),
                        new(mapped.Message, mapped.Issues.Select(issue => new FieldMappingIssue(issue.Field, issue.Reason, issue.Required)).ToImmutableArray()), token);
                    if (mapped.Message is null)
                    { throw Error("mapping.required", id, sourceId); }
                    message = mapped.Message;
                }
                token.ThrowIfCancellationRequested();
                activeNode = path.Nodes[^1];
                phase = "output.configuration";
                var output = nodes[activeNode];
                RoutePlanCompiler.ValidateOutputFields(output, fields, route.Key, sourceId);
                // Shared compiler performs exactly the same connection, header, topic and payload checks.
                // No mapping stages here: they were already executed above, once per path.
                var configuration = RoutePlanCompiler.Output(output, [selected.Source.UnitId.Key], [], snapshot, route.Key);
                var protocol = RuntimeConnectionReferences.Protocol(new(RuntimeUnitId.Create(output.Type, route.Key + "/" + output.Id), configuration));
                configuration = protocol.Configuration;
                RoutePlanCompiler.ValidateOutputFields(output with { Config = configuration }, fields, route.Key, sourceId);
                phase = "output.payload";
                var deliveryId = Guid.CreateVersion7();
                Guid? testSendId = capture is null ? null : Guid.NewGuid();
                byte[] body;
                if (protocol.Id.Kind == "output.http")
                {
                    var settings = HttpOutputSettings.Parse(configuration);
                    body = OutputPayloadFormat.Parse(settings.Payload).Serialize(message, deliveryId);
                    display.Output(state, activeNode, protocol.Id.Kind, deliveryId, settings.Path, null,
                        settings.Headers.SetItem("Idempotency-Key", deliveryId.ToString("D")).SetItem("Content-Type", "application/json"), body, testSendId);
                }
                else
                {
                    var settings = MqttOutputSettings.Parse(configuration);
                    body = OutputPayloadFormat.Parse(settings.Payload).Serialize(message, deliveryId);
                    display.Output(state, activeNode, protocol.Id.Kind, deliveryId, settings.TopicFor(message.DeviceKey), settings.Qos,
                        ImmutableDictionary<string, string>.Empty, body, testSendId);
                }
                if (state.Output?.TestSendId is { } capturedId &&
                    !capture!.TryAdd(capturedId, snapshot.Version, sourceId, sample.SourceMessageId, sample.CollectedAt,
                        path.Nodes, configuration, state.Output, body))
                { state.Output = state.Output with { TestSendId = null }; }
                display.Complete(state);
            }
            catch (RouteTopologyException error)
            { display.Fail(state, error.Issue); }
            catch (InputMessageSizeException)
            { display.Fail(state, new("preview.size", route.Key, activeNode, sourceId)); }
            catch (Exception error) when (ConfigurationError(error))
            { display.Fail(state, new(phase, route.Key, activeNode, sourceId)); }
        }
        token.ThrowIfCancellationRequested();
        return display.Result();

        RouteTopologyException Error(string code, string? node, long source) => new(new(code, route.Key, node, source));
    }

    private static bool ConfigurationError(Exception error) => error is ArgumentException or JsonException or InvalidOperationException or FormatException or RuntimeUnitBuildException;
}
