using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TenonEdge.Abstractions.Parameters;
using TenonEdge.Abstractions.Plans;
using TenonEdge.Host.Features.Parameters;
using TenonEdge.Host.Features.Runtime;
using TenonEdge.Runtime.Outputs;

namespace TenonEdge.Host.Features.Routes;

/// <summary>参数原文与连接引用分开保存，历史导出无需运行模块或猜测内部编号。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record RouteModuleOutputDraft(long? ConnectionId, JsonElement Parameters, Dictionary<string, long>? Connections = null)
{
    internal IEnumerable<long> ConnectionIds => (Connections?.Values ?? Enumerable.Empty<long>())
        .Concat(ConnectionId is { } id ? [id] : []);

    internal RouteModuleOutputDraft Normalize(string kind, int version, OutputModuleCatalog? catalog)
    {
        if (!OutputModuleCatalog.IsModuleKind(kind) || version < 1 || ConnectionId is < 1 or > 9007199254740991L ||
            Parameters.ValueKind != JsonValueKind.Object || Parameters.EnumerateObject().Any(property => property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array))
        { throw new ArgumentException("输出模块草稿格式无效。"); }
        var combined = ModuleDraftConnections.Combine(Parameters, Connections);
        if (Encoding.UTF8.GetByteCount(combined.GetRawText()) > ParameterSchema.MaximumConfigurationBytes ||
            combined.EnumerateObject().Count() > ParameterSchema.MaximumParameters)
        { throw new ArgumentException("输出模块参数超过上限。"); }
        _ = CanonicalJson.Serialize(combined); // Duplicate JSON properties are not a draft placeholder.
        if (catalog is not null)
        {
            var schema = catalog.Modules.SingleOrDefault(module => module.Kind == kind && module.Version == version)?.Parameters
                ?? throw new ArgumentException("未安装对应输出模块或版本。");
            var references = schema.Parameters.Where(field => field.Type == ParameterType.ConnectionReference).Select(field => field.Name).ToHashSet(StringComparer.Ordinal);
            if (Parameters.EnumerateObject().Any(property => references.Contains(property.Name)) ||
                Connections?.Keys.Any(name => !references.Contains(name)) == true ||
                schema.Validate(combined).Any(issue => issue.Code is not ("parameter.required" or "parameter.range" or "parameter.integer" or
                    "parameter.number" or "parameter.type" or "parameter.choice" or "parameter.connection")))
            { throw new ArgumentException("输出模块参数包含未知、重复、超限或未分离的连接引用。"); }
        }
        return this with { Parameters = Parameters.Clone(), Connections = Connections?.ToDictionary(StringComparer.Ordinal) };
    }

    internal JsonElement Bind(RouteGraphNode node, string route, string[] inputs, JsonElement[] stages, bool extended, RouteCompilationSnapshot snapshot)
    {
        var config = new Dictionary<string, object?>
        {
            ["connectionId"] = ConnectionId,
            ["inputs"] = inputs,
            ["outputModule"] = new
            {
                version = node.Version,
                parameters = ModuleDraftConnections.Combine(Parameters, Connections),
                connections = ModuleDraftConnections.Freeze(Connections ?? [], snapshot.Connections, snapshot.MqttCredentials, requireEnabled: true),
            },
        };
        if (stages.Length > 0)
        { config[extended ? "transformStages" : "mappingStages"] = stages; }
        var unit = snapshot.OutputsCatalog.WithFrozenProtocol(new(RuntimeUnitId.Create(node.Type, route + "/" + node.Id),
            JsonSerializer.SerializeToElement(config, JsonSerializerOptions.Web)));
        // Preview also needs the full native secret/address/identity checks, not merely a module's declaration.
        try
        {
            _ = RuntimePlanDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(new
            { schemaVersion = 1, units = new[] { new { kind = unit.Id.Kind, key = unit.Id.Key, configuration = unit.Configuration } } }),
                snapshot.Modules, snapshot.InputsCatalog, snapshot.OutputsCatalog);
        }
        catch (RuntimePlanDocumentException)
        { throw new ArgumentException("输出模块绑定的协议配置无效。"); }
        return unit.Configuration;
    }
}
