using System.Text.Json.Serialization;

namespace TenonEdge.Host.Features.Routes;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record RouteDraftRequest(string ExpectedVersion, string Key, string Name, bool Enabled, RouteGraph Graph, string? Description = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record RouteDraftCopyRequest(string ExpectedVersion, string Key, string Name);
internal sealed record RouteDraft(long Id, string Key, string Name, string? Description, bool Enabled, RouteGraph Graph, string? UpdatedBy, long UpdatedAt);
internal sealed record RouteDraftSummary(long Id, string Key, string Name, string? Description, bool Enabled, int NodeCount, string? UpdatedBy, long UpdatedAt);
internal sealed record RouteDraftList(string Version, RouteDraftSummary[] Routes);
internal sealed record RouteDraftResult(string Version, RouteDraft Route);
