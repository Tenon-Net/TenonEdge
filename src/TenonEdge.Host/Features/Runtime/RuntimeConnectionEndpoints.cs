using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Data.Sqlite;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Host.Bootstrap;
using TenonEdge.Host.Features.Configuration;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Host.Storage;
using TenonEdge.Runtime.Deliveries;
using TenonEdge.Runtime.Lifecycle;

namespace TenonEdge.Host.Features.Runtime;

internal sealed record RuntimeConnectionList(long? Revision, string DraftVersion, DateTimeOffset CapturedAt,
    bool QueueAvailable, RuntimeConnectionInfo[] Connections);
internal sealed record RuntimeConnectionInfo(long Id, ConnectionListDefinition? Effective, ConnectionListDefinition? Draft,
    bool? DraftChanged, string State, ConnectionListUser[] Users, ConnectionListUser[] DraftUsers, long? PendingCount,
    DateTimeOffset? LastSuccessAt, string? LastErrorCode, DateTimeOffset? LastErrorAt);

internal static class RuntimeConnectionEndpoints
{
    public static RouteGroupBuilder MapRuntimeConnectionEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/runtime/connections", List).WithTags("Runtime").WithName("getRuntimeConnections")
            .WithSummary("核对有效与草稿连接、正式通信事实、使用者及真实待发计数，不触发网络操作");
        return api;
    }

    private static Results<Ok<RuntimeConnectionList>, ProblemHttpResult> List(string? expectedDraftVersion,
        RuntimeCoordinator coordinator, RuntimeVersionStore versions, ConfigDatabase database, GatewayIdentity identity,
        ISharedConnections shared, DeliveryCache cache, TimeProvider time, HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (expectedDraftVersion is { Length: > 64 })
        { return ApiProblems.Problem(400, "runtime.invalidConnectionQuery", "草稿版本无效。"); }
        try
        {
            var runtime = coordinator.Runtime;
            var status = runtime.GetStatus();
            if (status.ActiveRevision is null && versions.ReadPointers().EffectiveRevision is not null)
            { return Unavailable(); }
            RuntimePlanDocument? plan = null;
            ConfigurationCheckpoint? effective = null;
            if (status.ActiveRevision is { } revision)
            {
                var snapshot = versions.Load(revision) ?? throw new RuntimePlanDocumentException("运行快照不存在。");
                (plan, effective) = RuntimeSnapshotDocument.ReadForExecution(snapshot);
            }
            var draft = database.InTransaction(client => ConfigurationCheckpoint.Read(client, ConfigurationDraft.ReadVersion(client)));
            if (expectedDraftVersion is not null && expectedDraftVersion != draft.Version)
            { return Changed(); }
            var catalog = RuntimeConnectionCatalog.Read(plan, effective, identity.NodeId);
            var effectiveDrafts = effective?.Connections.ToDictionary(connection => connection.Id);
            var drafts = draft.Connections.ToDictionary(connection => connection.Id);
            var current = draft.Connections.ToDictionary(connection => connection.Id, RuntimeConnectionCatalog.Describe);
            var currentUsers = RuntimeConnectionCatalog.ReadUsers(draft);
            var ids = catalog.Keys.Concat(current.Keys).Distinct().Order().ToArray();
            IReadOnlyDictionary<long, long>? counts = null;
            try
            { counts = cache.GetPendingCounts(ids); }
            catch (Exception error) when (error is SqliteException or IOException or UnauthorizedAccessException or InvalidOperationException)
            { /* 保留配置/通信诊断，队列不可读明确为空，不能伪造0。 */ }
            var senders = runtime.GetOutputSenders().ToDictionary(item => item.ConnectionId, item => item.Sender);
            var items = ids.Select(id =>
            {
                var entry = catalog.GetValueOrDefault(id);
                var definition = entry?.Definition;
                var activeUsers = entry?.Users.Any(user => user.Active) == true;
                var pending = counts?.GetValueOrDefault(id);
                SharedConnectionObservation? connection = null;
                if (entry?.Identities.SingleOrDefault() is { Protocol: not null } parameters)
                { connection = shared.Observe(id, parameters.Protocol, parameters.Identity); }
                var unitIds = entry?.Units.Select(unit => unit.Id).ToHashSet() ?? [];
                if (senders.GetValueOrDefault(id)?.UnitId is { } retainedId)
                { unitIds.Add(retainedId); }
                var units = status.Units.Where(unit => unitIds.Contains(unit.Id)).ToArray();
                var unavailable = units.Length > 0 && units.All(unit => unit.State != RuntimeUnitState.Running);
                var state = State(definition, activeUsers, pending, connection, unavailable);
                var observed = connection?.Observation;
                var fault = unavailable ? units.Where(unit => unit.LastErrorAt is not null).MaxBy(unit => unit.LastErrorAt) : null;
                var useFault = fault is not null && (observed?.LastErrorAt is null || fault.LastErrorAt > observed.LastErrorAt);
                return new RuntimeConnectionInfo(id, definition, current.GetValueOrDefault(id),
                    definition is not null && effectiveDrafts is null ? null :
                        RuntimeConnectionCatalog.DraftChanged(effectiveDrafts?.GetValueOrDefault(id), drafts.GetValueOrDefault(id)), state,
                    entry?.Users.ToArray() ?? [], currentUsers.GetValueOrDefault(id) ?? [],
                    (definition ?? current.GetValueOrDefault(id))?.Kind == "modbus" ? null : pending,
                    observed?.LastSuccessAt, useFault ? "runtime.unitUnavailable" : observed?.LastErrorCode,
                    useFault ? fault!.LastErrorAt : observed?.LastErrorAt);
            }).ToArray();
            if (runtime.ActiveRevision != status.ActiveRevision || database.InTransaction(ConfigurationDraft.ReadVersion) != draft.Version)
            { return Changed(); }
            return TypedResults.Ok(new RuntimeConnectionList(status.ActiveRevision, draft.Version, time.GetUtcNow(), counts is not null, items));
        }
        catch (RuntimePlanDocumentException) { return Unavailable(); }
        catch (Exception error) when (error is SqliteException or IOException or UnauthorizedAccessException)
        { return ApiProblems.Problem(503, "runtime.configurationUnavailable", "连接配置暂不可读取，请重试；不使用过期草稿替代。"); }
    }

    internal static string State(ConnectionListDefinition? effective, bool activeUsers, long? pending,
        SharedConnectionObservation? connection, bool unavailable)
    {
        if (effective is null)
        { return "notApplied"; }
        if (!effective.Enabled)
        { return "disabled"; }
        if (!activeUsers && (effective.Kind == "modbus" || pending is not > 0))
        { return pending is null && effective.Kind != "modbus" ? "unknown" : "unused"; }
        if (unavailable || connection?.Closing == true)
        { return "degraded"; }
        if (connection is null)
        { return "connecting"; }
        return connection.Observation?.State switch
        {
            ConnectionCommunicationState.Connected => "connected",
            ConnectionCommunicationState.Connecting => "connecting",
            ConnectionCommunicationState.Degraded => "degraded",
            _ => "unknown"
        };
    }

    private static ProblemHttpResult Changed() => ApiProblems.Problem(409, "runtime.connectionVersionChanged", "有效配置或草稿已变化，请刷新连接列表。");
    private static ProblemHttpResult Unavailable() => ApiProblems.Problem(409, "runtime.snapshotUnavailable", "有效运行快照不可读取，不能把草稿当作运行配置。");
}
