using System.Text.Json;
using System.Text.Json.Serialization;
using SqlSugar;
using TenonEdge.Host.Bootstrap;
using TenonEdge.Host.Features.Audit;
using TenonEdge.Host.Features.Routes;
using TenonEdge.Host.Security;
using TenonEdge.Host.Storage;
using TenonEdge.Runtime.Diagnostics;

namespace TenonEdge.Host.Features.SystemInfo;

/// <summary>系统运行参数不是草稿。提交与审计同事务，提交成功后发布不可变值；不持有请求作用域或事务。</summary>
internal sealed class RuntimeParameterStore(ConfigDatabase database, AuditTrail audit, DataRetentionSettings deployment,
    TimeProvider time, AuditRetentionGate retentionGate, RuntimeMessageCache messages, InFlightBudget inFlight, InputChannelLimit inputChannels)
{
    private readonly Lock _gate = new();
    private RuntimeParameterState? _current;
    public const int MaximumPreviewGlobal = 64;

    public RuntimeParameterState Read()
    {
        if (Volatile.Read(ref _current) is { } current)
        { return current; }
        lock (_gate)
        {
            if (_current is not null)
            { return _current; }
            using var client = database.CreateIsolatedClient();
            var row = client.Queryable<RuntimeParameterRecord>().InSingle(1);
            try
            {
                var values = row is null ? new RuntimeParameterValues(180, 4, 2)
                    : new(row.AuditRetentionDays, row.PreviewGlobalLimit, row.PreviewSessionLimit, row.DebugObjectBytes, row.DebugGlobalBytes, row.DebugObjectRecords, row.InFlightBytes, row.InputChannelCapacity,
                        row.PlanSources, row.PlanConnections, row.PlanModbusConnections, row.PlanPathsPerSource, row.PlanTotalPaths);
                Validate(values, DataRetentionSettings.MinimumAuditRetentionDays);
                if (row is not null && !Guid.TryParseExact(row.Version, "N", out _))
                { throw new ArgumentException("持久版本无效。"); }
                var minimum = (int)deployment.AuditRetention.TotalDays;
                var state = new RuntimeParameterState(row?.Version, values with { AuditRetentionDays = Math.Max(minimum, values.AuditRetentionDays) }, minimum);
                messages.Configure(values.DebugObjectBytes, values.DebugGlobalBytes, values.DebugObjectRecords);
                inFlight.ConfigureCapacity(values.InFlightBytes);
                inputChannels.Configure(values.InputChannelCapacity);
                Volatile.Write(ref _current, state);
                return state;
            }
            catch (ArgumentException exception)
            { throw new HostStartupException(HostExitCodes.ConfigStoreUnavailable, $"持久运行参数无效，未按默认值运行：{exception.Message}"); }
        }
    }

    public RuntimeParameterState Write(RuntimeParameterRequest request, ManagementActor actor)
    {
        if (!request.Confirmed)
        { throw new ArgumentException("须明确确认运行参数变更。"); }
        Validate(request.Values, (int)deployment.AuditRetention.TotalDays);
        lock (_gate)
        {
            var current = Read();
            if (request.ExpectedVersion != current.Version)
            { throw new RuntimeParameterConflictException(); }
            // Retention changes must not race an old-cutoff purge. Export generation
            // also holds this gate; reject immediately rather than queue a write.
            using var retention = retentionGate.TryEnterPolicyChange() ?? throw new RuntimeParameterBusyException();
            var next = database.InTransaction(client =>
            {
                var account = actor.RequireAdministrator(client, time.GetUtcNow().ToUnixTimeMilliseconds()).Audit;
                var row = client.Queryable<RuntimeParameterRecord>().InSingle(1);
                if (row?.Version != current.Version)
                { throw new RuntimeParameterConflictException(); }
                if (request.Values == current.Values)
                { return current; }
                var existed = row is not null;
                row ??= new RuntimeParameterRecord();
                row.Version = Guid.NewGuid().ToString("N");
                row.AuditRetentionDays = request.Values.AuditRetentionDays;
                row.PreviewGlobalLimit = request.Values.PreviewGlobalLimit;
                row.PreviewSessionLimit = request.Values.PreviewSessionLimit;
                row.DebugObjectBytes = request.Values.DebugObjectBytes;
                row.DebugGlobalBytes = request.Values.DebugGlobalBytes;
                row.DebugObjectRecords = request.Values.DebugObjectRecords;
                row.InFlightBytes = request.Values.InFlightBytes;
                row.InputChannelCapacity = request.Values.InputChannelCapacity;
                row.PlanSources = request.Values.PlanSources;
                row.PlanConnections = request.Values.PlanConnections;
                row.PlanModbusConnections = request.Values.PlanModbusConnections;
                row.PlanPathsPerSource = request.Values.PlanPathsPerSource;
                row.PlanTotalPaths = request.Values.PlanTotalPaths;
                row.UpdatedAt = time.GetUtcNow().ToUnixTimeMilliseconds();
                if (existed)
                { client.Updateable(row).ExecuteCommand(); }
                else
                { client.Insertable(row).ExecuteCommand(); }
                var result = new RuntimeParameterState(row.Version, request.Values, current.DeploymentAuditMinimumDays);
                audit.Append(client, new AuditEntry("system.runtimeParametersChanged", AuditResults.Success, account, actor.SourceIp,
                    "runtimeParameters", JsonSerializer.Serialize(new { previous = current, current = result })));
                return result;
            });
            // Keep the update lock and retention gate across COMMIT/publication so
            // concurrent writes cannot publish out of order or purge using old values.
            messages.Configure(next.Values.DebugObjectBytes, next.Values.DebugGlobalBytes, next.Values.DebugObjectRecords);
            inFlight.ConfigureCapacity(next.Values.InFlightBytes);
            inputChannels.Configure(next.Values.InputChannelCapacity);
            Volatile.Write(ref _current, next);
            return next;
        }
    }

    // 使用调用者已有的草稿/配置事务读取，同一次编译或提交不会混用不同版本的策略。
    // 不取得参数发布锁，避免与参数写入的锁/数据库事务顺序反转。
    internal static RouteCompilationLimits ReadPlanLimits(SqlSugarClient client)
    {
        var row = client.Queryable<RuntimeParameterRecord>().InSingle(1);
        var limits = row is null ? RouteCompilationLimits.Default
            : new(row.PlanSources, row.PlanConnections, row.PlanModbusConnections, row.PlanPathsPerSource, row.PlanTotalPaths);
        limits.Validate();
        return limits;
    }

    private static void Validate(RuntimeParameterValues values, int minimumAuditDays)
    {
        ArgumentNullException.ThrowIfNull(values);
        values.PlanLimits.Validate();
        InputChannelLimit.Validate(values.InputChannelCapacity);
        if (values.InFlightBytes is < 16 * 1024 * 1024 or > 1024 * 1024 * 1024)
        { throw new ArgumentException("在途预算须为16777216—1073741824字节，含固定8MiB原始缓冲分区；调高须在本机实测。"); }
        RuntimeMessageCache.ValidateLimits(values.DebugObjectBytes, values.DebugGlobalBytes, values.DebugObjectRecords);
        if (values.AuditRetentionDays < minimumAuditDays || values.AuditRetentionDays > DataRetentionSettings.MaximumAuditRetentionDays)
        { throw new ArgumentException($"审计保留期须为 {minimumAuditDays}—{DataRetentionSettings.MaximumAuditRetentionDays} 天，不得短于部署下限。"); }
        if (values.PreviewGlobalLimit is < 1 or > MaximumPreviewGlobal || values.PreviewSessionLimit < 1 || values.PreviewSessionLimit > values.PreviewGlobalLimit)
        { throw new ArgumentException($"预览并发全局须为1—{MaximumPreviewGlobal}，每个会话须为1—全局上限；调高须在本机实测。"); }
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record RuntimeParameterValues(int AuditRetentionDays, int PreviewGlobalLimit, int PreviewSessionLimit,
    [property: JsonRequired] int DebugObjectBytes = RuntimeMessageCache.MaximumObjectBytes,
    [property: JsonRequired] int DebugGlobalBytes = RuntimeMessageCache.MaximumGlobalBytes,
    [property: JsonRequired] int DebugObjectRecords = RuntimeMessageCache.MaximumObjectRecords,
    [property: JsonRequired] long InFlightBytes = 64L * 1024 * 1024,
    [property: JsonRequired] int InputChannelCapacity = 100,
    [property: JsonRequired] int PlanSources = 2000,
    [property: JsonRequired] int PlanConnections = 500,
    [property: JsonRequired] int PlanModbusConnections = 256,
    [property: JsonRequired] int PlanPathsPerSource = 32,
    [property: JsonRequired] int PlanTotalPaths = 20000)
{
    [JsonIgnore] public RouteCompilationLimits PlanLimits => new(PlanSources, PlanConnections, PlanModbusConnections, PlanPathsPerSource, PlanTotalPaths);
    [JsonIgnore] public PreviewOperationLimits Preview => new(PreviewGlobalLimit, PreviewSessionLimit);
}
internal sealed record RuntimeParameterState(string? Version, RuntimeParameterValues Values, int DeploymentAuditMinimumDays);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record RuntimeParameterRequest(RuntimeParameterValues Values, [property: JsonRequired] string? ExpectedVersion, bool Confirmed);
internal sealed class RuntimeParameterConflictException : Exception;
internal sealed class RuntimeParameterBusyException : Exception;

[SugarTable("runtime_parameters")]
internal sealed class RuntimeParameterRecord
{
    [SugarColumn(ColumnName = "id", IsPrimaryKey = true)] public int Id { get; set; } = 1;
    [SugarColumn(ColumnName = "version")] public string Version { get; set; } = string.Empty;
    [SugarColumn(ColumnName = "audit_retention_days")] public int AuditRetentionDays { get; set; }
    [SugarColumn(ColumnName = "preview_global_limit")] public int PreviewGlobalLimit { get; set; }
    [SugarColumn(ColumnName = "preview_session_limit")] public int PreviewSessionLimit { get; set; }
    [SugarColumn(ColumnName = "debug_object_bytes")] public int DebugObjectBytes { get; set; } = RuntimeMessageCache.MaximumObjectBytes;
    [SugarColumn(ColumnName = "debug_global_bytes")] public int DebugGlobalBytes { get; set; } = RuntimeMessageCache.MaximumGlobalBytes;
    [SugarColumn(ColumnName = "debug_object_records")] public int DebugObjectRecords { get; set; } = RuntimeMessageCache.MaximumObjectRecords;
    [SugarColumn(ColumnName = "in_flight_bytes")] public long InFlightBytes { get; set; } = 64L * 1024 * 1024;
    [SugarColumn(ColumnName = "input_channel_capacity")] public int InputChannelCapacity { get; set; } = 100;
    [SugarColumn(ColumnName = "plan_sources")] public int PlanSources { get; set; } = 2000;
    [SugarColumn(ColumnName = "plan_connections")] public int PlanConnections { get; set; } = 500;
    [SugarColumn(ColumnName = "plan_modbus_connections")] public int PlanModbusConnections { get; set; } = 256;
    [SugarColumn(ColumnName = "plan_paths_per_source")] public int PlanPathsPerSource { get; set; } = 32;
    [SugarColumn(ColumnName = "plan_total_paths")] public int PlanTotalPaths { get; set; } = 20000;
    [SugarColumn(ColumnName = "updated_at")] public long UpdatedAt { get; set; }
}
