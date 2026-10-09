using System.Collections.Immutable;
using TenonEdge.Abstractions.Plans;
using TenonEdge.Runtime.Mapping;

namespace TenonEdge.Host.Features.Routes;

// 编译阶段的纯拓扑快照，不是 React Flow 状态，也不是对外草稿/运行计划格式。
// 配置与布局由后续草稿解析器负责；这里不读取数据库、凭证或网络。
internal sealed record RouteTopology(string Key, bool Enabled, int SchemaVersion, ImmutableArray<RouteTopologyNode> Nodes,
    ImmutableArray<RouteTopologyEdge> Edges);
internal sealed record RouteTopologyNode(string Id, string Kind, int Version, ImmutableArray<long> SourceIds);
internal sealed record RouteTopologyEdge(string Source, string Target);
internal sealed record RouteTopologySource(long Id, RuntimeUnitId UnitId, bool Enabled, ImmutableArray<MappedFieldSchema> Fields);
internal sealed record RouteTopologyPath(string RouteKey, long SourceId, ImmutableArray<string> Nodes);
internal sealed record RouteTopologyResult(ImmutableArray<RouteTopologyPath> Paths, ImmutableDictionary<long, int> SourcePathCounts);
internal sealed record RouteTopologyIssue(string Code, string RouteKey, string? NodeId = null, long? SourceId = null, int? EdgeIndex = null, string? FieldKey = null);
internal sealed class RouteTopologyException(RouteTopologyIssue issue) : ArgumentException("路线拓扑不能编译，请检查结构化错误定位。")
{
    public RouteTopologyIssue Issue { get; } = issue;
}
