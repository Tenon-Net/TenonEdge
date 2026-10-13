using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Data.Sqlite;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Runtime.Deliveries;
using TenonEdge.Runtime.Diagnostics;
using TenonEdge.Runtime.Lifecycle;

namespace TenonEdge.Host.Features.Runtime;

internal static class RuntimeStatusEndpoints
{
    public static RouteGroupBuilder MapRuntimeStatusEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/runtime/status", Get)
            .WithTags("Runtime")
            .WithName("getRuntimeStatus")
            .WithSummary("查看运行状态、存储保护和累计计数");
        api.MapGet("/runtime/trends", (RuntimeTrendHistory history) => TypedResults.Ok(history.Snapshot()))
            .WithTags("Runtime")
            .WithName("getRuntimeTrends")
            .WithSummary("查看本进程最近一小时的收发和失败趋势计数");
        return api;
    }

    private static Ok<RuntimeDiagnosticsResponse> Get(RuntimeCoordinator coordinator, DeliveryCache deliveries, IClockSyncStatus clock, HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        IReadOnlyDictionary<string, long>? counters = null;
        var cache = deliveries.GetStatus();
        if (cache.Store is not null)
        {
            try
            {
                counters = deliveries.GetPersistentCounters();
            }
            catch (Exception exception) when (exception is SqliteException or IOException or UnauthorizedAccessException)
            {
                // 空表示暂不可读取，不返回伪造的零计数；保留其他诊断，不让磁盘故障变成整页 500。
            }
        }

        return TypedResults.Ok(new RuntimeDiagnosticsResponse(coordinator.Status, coordinator.Runtime.GetStatus(),
            cache, deliveries.StorageProtection.Status, clock.State, counters, coordinator.Runtime.InFlight.Status,
            deliveries.GetUnpersistedCounters()));
    }
}

internal sealed record RuntimeDiagnosticsResponse(RuntimeCoordinatorStatus Coordinator, RuntimeStatusReport Runtime,
    DeliveryCacheStatus Queue, StorageProtectionStatus StorageProtection, ClockSyncState ClockSyncState,
    IReadOnlyDictionary<string, long>? PersistentCounters, InFlightBudgetStatus InFlight,
    IReadOnlyDictionary<string, long> UnpersistedCounters);
