using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.WebUtilities;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Host.Features.Connections;

namespace TenonEdge.Host.Features.Routes;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record RouteGraph(int SchemaVersion, RouteGraphNode[] Nodes, RouteGraphEdge[] Edges, RouteGraphLayout Layout);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record RouteGraphNode(string Id, string Type, int Version, string Name, JsonElement Config);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record RouteGraphEdge(string Source, string Target);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record RouteGraphLayout(Dictionary<string, RouteNodePosition> Nodes);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record RouteNodePosition(double X, double Y);
internal sealed record RouteGraphReference(string Kind, long TargetId);

/// <summary>只校验可安全保存的格式与资源边界；悬空、环、未选来源、缺失引用和未完成规则留给编译器。</summary>
internal static class RouteGraphFormat
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 16 };
    internal const int MaximumBytes = 1024 * 1024;

    public static RouteGraph Normalize(RouteGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        if (graph.SchemaVersion != 1 || graph.Nodes is null || graph.Nodes.Length > RouteTopologyCompiler.MaximumNodes ||
            graph.Edges is null || graph.Edges.Length > 2450 || graph.Layout?.Nodes is null)
        { throw new ArgumentException("图版本或大小无效。"); }
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var nodes = graph.Nodes.Select(node =>
        {
            if (node is null || !BusinessKeys.IsValid(node.Id) || !ids.Add(node.Id) || node.Version != 1 ||
                node.Name is null || node.Name.Length > 64 || node.Name.Any(char.IsControl))
            { throw new ArgumentException("节点标识、名称或版本无效。"); }
            return node with { Config = NormalizeConfig(node.Type, node.Config) };
        }).ToArray();
        foreach (var edge in graph.Edges)
        {
            if (edge is null || !BusinessKeys.IsValid(edge.Source) || !BusinessKeys.IsValid(edge.Target))
            { throw new ArgumentException("连线端点标识无效。"); }
        }
        foreach (var (id, position) in graph.Layout.Nodes)
        {
            if (!ids.Contains(id) || position is null || !double.IsFinite(position.X) || !double.IsFinite(position.Y) ||
                Math.Abs(position.X) > 10000000 || Math.Abs(position.Y) > 10000000)
            { throw new ArgumentException("布局坐标无效。"); }
        }
        var result = graph with { Nodes = nodes, Edges = graph.Edges.ToArray(), Layout = new(new(graph.Layout.Nodes, StringComparer.Ordinal)) };
        if (Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(result, Json)) > MaximumBytes)
        { throw new ArgumentException("路线图超过 1 MiB。"); }
        return result;
    }

    public static RouteGraphReference[] References(RouteGraph graph) => graph.Nodes.SelectMany(node =>
    {
        if (node.Type.StartsWith("input.", StringComparison.Ordinal))
        { return Read<RouteInputConfig>(node.Config).SourceIds.Select(id => new RouteGraphReference("input", id)); }
        if (node.Type.StartsWith("output.", StringComparison.Ordinal) && node.Config.TryGetProperty("connectionId", out var id) && id.ValueKind != JsonValueKind.Null)
        { return [new RouteGraphReference("connection", id.GetInt64())]; }
        return Enumerable.Empty<RouteGraphReference>();
    }).Distinct().ToArray();

    private static JsonElement NormalizeConfig(string kind, JsonElement config)
    {
        if (config.ValueKind != JsonValueKind.Object)
        { throw new ArgumentException("节点配置须为对象。"); }
        switch (kind)
        {
            case "input.modbus":
            case "input.mqtt":
            case "input.http":
            case "input.simulator":
                var input = Read<RouteInputConfig>(config);
                if (input.SourceIds is null || input.SourceIds.Length > 500 || input.SourceIds.Any(id => !ValidId(id)))
                { throw new ArgumentException("来源列表无效。"); }
                return JsonSerializer.SerializeToElement(input, Json);
            case "transform.map":
                var map = Read<RouteMapConfig>(config);
                if (map.Rules is null || map.Rules.Length > 1000)
                { throw new ArgumentException("映射规则数量无效。"); }
                foreach (var rule in map.Rules)
                {
                    if (rule is null)
                    { throw new ArgumentException("映射规则为空。"); }
                    Text(rule.Target, 64);
                    Text(rule.Type, 32);
                    Text(rule.SourceField, 64);
                    Text(rule.SourcePath, 1024);
                    Text(rule.Unit, 256);
                    Scalar(rule.Constant);
                }
                return JsonSerializer.SerializeToElement(map, Json);
            case "output.mqtt":
                var mqtt = Read<RouteMqttConfig>(config);
                Connection(mqtt.ConnectionId);
                Text(mqtt.Topic, 65535);
                Payload(mqtt.Payload);
                return JsonSerializer.SerializeToElement(mqtt, Json);
            case "output.http":
                var http = Read<RouteHttpConfig>(config);
                Connection(http.ConnectionId);
                Text(http.Path, 2048);
                Text(http.SuccessPath, 1024);
                Scalar(http.SuccessValue);
                Payload(http.Payload);
                if (http.Path is { } path)
                {
                    var query = path.IndexOf('?');
                    if (query >= 0 && QueryHelpers.ParseQuery(path[query..]).Keys.Any(ConnectionDraftValidation.IsSecretName) ||
                        Uri.TryCreate(path, UriKind.Absolute, out var absolute) && absolute.UserInfo.Length > 0)
                    { throw new ArgumentException("秘密地址或查询参数须改用连接凭证。"); }
                }
                if (http.Headers is { } headers)
                {
                    if (headers.Count > 100)
                    { throw new ArgumentException("请求头过多。"); }
                    foreach (var (key, value) in headers)
                    {
                        Text(key, 256);
                        Text(value, 16384);
                        if (ConnectionDraftValidation.IsSecretName(key) || key.Equals("Authorization", StringComparison.OrdinalIgnoreCase) ||
                            key.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase) || key.Equals("Cookie", StringComparison.OrdinalIgnoreCase))
                        { throw new ArgumentException("秘密请求头须使用连接凭证。"); }
                    }
                }
                return JsonSerializer.SerializeToElement(http, Json);
            case "output.tenon":
                var tenon = Read<RouteTenonConfig>(config);
                Connection(tenon.ConnectionId);
                return JsonSerializer.SerializeToElement(tenon, Json);
            default:
                throw new ArgumentException("未知节点类型。");
        }
    }

    private static void Payload(JsonElement payload)
    {
        if (payload.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        { return; }
        var format = Read<RoutePayloadConfig>(payload);
        Text(format.Mode, 32);
        if (format.Fields is null)
        { return; }
        if (format.Fields.Length > 1000)
        { throw new ArgumentException("组装字段过多。"); }
        foreach (var field in format.Fields)
        {
            if (field is null)
            { throw new ArgumentException("组装字段为空。"); }
            Text(field.Target, 64);
            Text(field.SourceField, 64);
            Text(field.Placeholder, 64);
            Scalar(field.Constant);
        }
    }

    private static T Read<T>(JsonElement value) where T : class => value.Deserialize<T>(Json) ?? throw new ArgumentException("配置为空。");
    private static bool ValidId(long id) => id is >= 1 and <= 9007199254740991L;
    private static void Connection(long? id) { if (id is { } value && !ValidId(value)) { throw new ArgumentException("连接编号无效。"); } }
    private static void Text(string? value, int maximum) { if (value?.Length > maximum) { throw new ArgumentException("配置文字过长。"); } }
    private static void Scalar(JsonElement value)
    {
        if (value.ValueKind is JsonValueKind.Array or JsonValueKind.Object ||
            value.ValueKind == JsonValueKind.String && value.GetString()!.Length > 65535)
        { throw new ArgumentException("固定值须为有界 JSON 标量。"); }
    }
}

internal sealed record RouteInputConfig(long[] SourceIds);
internal sealed record RouteMapConfig(RouteMapRule[] Rules);
internal sealed record RouteMapRule(string? Target = null, string? Type = null, string? SourceField = null, string? SourcePath = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] JsonElement Constant = default,
    string? Unit = null, bool Required = false, decimal? Scale = null, decimal? Offset = null, int? DecimalPlaces = null);
internal sealed record RouteMqttConfig(long? ConnectionId = null, string? Topic = null, int? Qos = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] JsonElement Payload = default);
internal sealed record RouteHttpConfig(long? ConnectionId = null, string? Path = null, Dictionary<string, string>? Headers = null,
    string? SuccessPath = null, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] JsonElement SuccessValue = default,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] JsonElement Payload = default);
internal sealed record RouteTenonConfig(long? ConnectionId = null);
internal sealed record RoutePayloadConfig(string? Mode = null, RoutePayloadField[]? Fields = null);
internal sealed record RoutePayloadField(string? Target = null, string? SourceField = null, string? Placeholder = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] JsonElement Constant = default, bool Required = false);
