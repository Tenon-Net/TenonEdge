using System.Text.Json;
using System.Text.Json.Serialization;
using SqlSugar;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Host.Bootstrap;
using TenonEdge.Host.Features.Audit;
using TenonEdge.Host.Storage;

namespace TenonEdge.Host.Features.SystemInfo;

/// <summary>配置库中的队列参数与审计同事务。读取租约只保护同步队列事务，不持有请求、数据库连接或事务。</summary>
internal sealed class QueueParameterStore(ConfigDatabase database, AuditTrail audit, TimeProvider time) : IDeliveryPolicySource
{
    private readonly Lock _gate = new();
    private Snapshot? _current;
    private int _readers;

    public QueueParameterState Read() => Load().State;
    DeliveryPolicy IDeliveryPolicySource.Read() => Load().Policy;

    public IDisposable AcquireRead()
    {
        _gate.Enter();
        try
        {
            _ = Load();
            _readers++;
            return new Lease(this);
        }
        catch { _gate.Exit(); throw; }
    }

    private Snapshot Load()
    {
        if (Volatile.Read(ref _current) is { } current)
        { return current; }
        lock (_gate)
        {
            if (_current is not null)
            { return _current; }
            using var client = database.CreateIsolatedClient();
            var row = client.Queryable<QueueParameterRecord>().InSingle(1);
            try
            {
                var values = row is null ? QueueParameterValues.Default
                    : JsonSerializer.Deserialize<QueueParameterValues>(row.ValuesJson) ?? throw new ArgumentException("队列参数为空。");
                values.Validate();
                if (row is not null && !Guid.TryParseExact(row.Version, "N", out _))
                { throw new ArgumentException("队列参数持久版本无效。"); }
                var loaded = new Snapshot(new(row?.Version, values), values.ToPolicy());
                Volatile.Write(ref _current, loaded);
                return loaded;
            }
            catch (Exception exception) when (exception is ArgumentException or JsonException)
            { throw new HostStartupException(HostExitCodes.ConfigStoreUnavailable, $"持久队列参数无效，未按默认值运行：{exception.Message}"); }
        }
    }

    public QueueParameterState Write(QueueParameterRequest request, AuditAccount account, string? sourceIp)
    {
        if (!request.Confirmed)
        { throw new ArgumentException("须明确确认队列参数变更与保留期风险。"); }
        ArgumentNullException.ThrowIfNull(request.Values);
        request.Values.Validate();
        if (!_gate.TryEnter())
        { throw new QueueParameterBusyException(); }
        try
        {
            // Also reject re-entrant changes from a synchronous queue callback.
            if (_readers != 0)
            { throw new QueueParameterBusyException(); }
            var current = Load();
            if (request.ExpectedVersion != current.State.Version)
            { throw new RuntimeParameterConflictException(); }
            var next = database.InTransaction(client =>
            {
                var row = client.Queryable<QueueParameterRecord>().InSingle(1);
                if (row?.Version != current.State.Version)
                { throw new RuntimeParameterConflictException(); }
                if (request.Values == current.State.Values)
                { return current; }
                var existed = row is not null;
                row ??= new QueueParameterRecord();
                row.Version = Guid.NewGuid().ToString("N");
                row.ValuesJson = JsonSerializer.Serialize(request.Values);
                row.UpdatedAt = time.GetUtcNow().ToUnixTimeMilliseconds();
                if (existed)
                { client.Updateable(row).ExecuteCommand(); }
                else
                { client.Insertable(row).ExecuteCommand(); }
                var result = new Snapshot(new(row.Version, request.Values), request.Values.ToPolicy());
                audit.Append(client, new AuditEntry("system.queueParametersChanged", AuditResults.Success, account, sourceIp,
                    "queueParameters", JsonSerializer.Serialize(new { previous = current.State, current = result.State })));
                return result;
            });
            // Gate covers durable COMMIT and publication. An old-policy queue
            // transaction cannot commit after this update reports success.
            Volatile.Write(ref _current, next);
            return next.State;
        }
        finally { _gate.Exit(); }
    }

    private sealed record Snapshot(QueueParameterState State, DeliveryPolicy Policy);
    private sealed class Lease(QueueParameterStore owner) : IDisposable
    {
        private QueueParameterStore? _owner = owner;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _owner, null) is { } current)
            { current._readers--; current._gate.Exit(); }
        }
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record QueueParameterValues(
    [property: JsonRequired] int MaximumPendingPerTarget,
    [property: JsonRequired] long MaximumPendingBytesPerTarget,
    [property: JsonRequired] long MaximumQueueBytes,
    [property: JsonRequired] int PendingRetentionHours,
    [property: JsonRequired] int MaximumFailedPerTarget,
    [property: JsonRequired] int MaximumFailedGlobal,
    [property: JsonRequired] int FailedRetentionHours,
    [property: JsonRequired] long MinimumDiskFreeBytes,
    [property: JsonRequired] int MinimumDiskFreePercent)
{
    public static QueueParameterValues Default { get; } = new(10000, 64L * 1024 * 1024, 256L * 1024 * 1024, 24, 100, 1000, 168, 500_000_000, 5);
    public const long MaximumBytes = 1024L * 1024 * 1024 * 1024;
    public void Validate()
    {
        if (MaximumPendingPerTarget is < 1 or > 1_000_000 || MaximumFailedPerTarget is < 1 or > 1_000_000 || MaximumFailedGlobal is < 1 or > 1_000_000)
        { throw new ArgumentException("待发/失败区条数上限须为1—1000000；不是硬件容量承诺。"); }
        if (MaximumPendingBytesPerTarget is < 1 or > MaximumBytes || MaximumQueueBytes is < 1 or > MaximumBytes || MinimumDiskFreeBytes is < 0 or > MaximumBytes)
        { throw new ArgumentException("队列字节上限须为1—1TiB，磁盘预留须为0—1TiB。"); }
        if (PendingRetentionHours is < 1 or > 87600 || FailedRetentionHours is < 1 or > 87600 || MinimumDiskFreePercent is < 0 or > 99)
        { throw new ArgumentException("保留期须为1—87600小时，磁盘预留百分比须为0—99。"); }
    }
    public DeliveryPolicy ToPolicy() => new()
    {
        MaximumPendingPerTarget = MaximumPendingPerTarget,
        MaximumPendingBytesPerTarget = MaximumPendingBytesPerTarget,
        MaximumQueueBytes = MaximumQueueBytes,
        PendingRetention = TimeSpan.FromHours(PendingRetentionHours),
        MaximumFailedPerTarget = MaximumFailedPerTarget,
        MaximumFailedGlobal = MaximumFailedGlobal,
        FailedRetention = TimeSpan.FromHours(FailedRetentionHours),
        MinimumDiskFreeBytes = MinimumDiskFreeBytes,
        MinimumDiskFreeFraction = MinimumDiskFreePercent / 100d,
    };
}
internal sealed record QueueParameterState(string? Version, QueueParameterValues Values);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record QueueParameterRequest(QueueParameterValues Values, [property: JsonRequired] string? ExpectedVersion, bool Confirmed);
internal sealed class QueueParameterBusyException : Exception;
[SugarTable("queue_parameters")]
internal sealed class QueueParameterRecord
{
    [SugarColumn(ColumnName = "id", IsPrimaryKey = true)] public int Id { get; set; } = 1;
    [SugarColumn(ColumnName = "version")] public string Version { get; set; } = string.Empty;
    [SugarColumn(ColumnName = "values_json")] public string ValuesJson { get; set; } = string.Empty;
    [SugarColumn(ColumnName = "updated_at")] public long UpdatedAt { get; set; }
}
