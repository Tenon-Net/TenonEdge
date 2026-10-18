using System.Collections.Immutable;
using System.Text.Json;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Runtime.Mapping;
using TenonEdge.Runtime.Transforms;

namespace TenonEdge.Host.Features.Routes;

internal sealed class RouteFieldPropagation(IReadOnlyDictionary<string, RouteGraphNode> nodes, TransformCatalog? transforms = null)
{
    private readonly Dictionary<string, IMessageTransform> _mappings = new(StringComparer.Ordinal);
    private readonly HashSet<(string Node, string Upstream)> _checked = [];

    internal (string Upstream, ImmutableArray<MappedFieldSchema> Fields) Propagate(RouteTopologyPath path,
        ImmutableArray<MappedFieldSchema> fields, CancellationToken token)
    {
        var upstream = "source/" + path.SourceId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        foreach (var id in path.Nodes.Skip(1).SkipLast(1))
        {
            token.ThrowIfCancellationRequested();
            if (!_mappings.TryGetValue(id, out var mapping))
            {
                try
                { mapping = (transforms ?? TransformCatalog.BuiltIn).Build(nodes[id].Type, nodes[id].Version, nodes[id].Config); }
                catch (Exception error) when (error is ArgumentException or JsonException or InvalidOperationException or FormatException)
                { throw new RouteTopologyException(new("mapping.configuration", path.RouteKey, id, path.SourceId)); }
                _mappings.Add(id, mapping);
            }
            if (!_checked.Contains((id, upstream)))
            {
                var known = fields.Select(field => field.Key).ToHashSet(StringComparer.Ordinal);
                foreach (var referenced in mapping.ReferencedFields)
                { if (!known.Contains(referenced)) { throw new RouteTopologyException(new("field.missing", path.RouteKey, id, path.SourceId, FieldKey: referenced)); } }
            }
            _checked.Add((id, upstream));
            fields = mapping.Schema.Select(field => new MappedFieldSchema(field.Key, field.Type, field.Unit)).ToImmutableArray();
            upstream = "map/" + id;
        }
        return (upstream, fields);
    }
}
