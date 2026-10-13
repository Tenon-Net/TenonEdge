using Microsoft.AspNetCore.Http.HttpResults;
using TenonEdge.Abstractions.Messages;
using TenonEdge.Abstractions.Plans;
using TenonEdge.Adapters.Http;
using TenonEdge.Adapters.Modbus;
using TenonEdge.Adapters.Simulator;
using TenonEdge.Host.Bootstrap;
using TenonEdge.Host.Features.Devices;
using TenonEdge.Host.Features.Inputs;
using TenonEdge.Runtime.Diagnostics;
using TenonEdge.Runtime.Lifecycle;

namespace TenonEdge.Host.Features.Runtime;

internal sealed record RuntimeDeviceList(long? Revision, RuntimeDeviceInfo[] Devices, CurrentValueCacheStatus Cache);
internal sealed record RuntimeDeviceInfo(string Key, string Name, string Kind, string Health, bool DraftOnly,
    bool RemovedFromDraft, RuntimeDeviceSource[] Sources);
internal sealed record RuntimeDeviceSource(string Key, string Name, string Kind, bool Enabled, int? PeriodMs,
    int Fields, RuntimeUnitState? State, InputReadSnapshot? Reads, DateTimeOffset? LastReceivedAt);
internal sealed record RuntimePointValue(string Key, string? Name, string? Unit, bool Enabled, CurrentFieldValue Value);
internal sealed record RuntimeCurrentValues(long Revision, string DeviceKey, string InputKey, RuntimePointValue[] Items,
    string? NextKey, int Total);

/// <summary>元数据来自正在运行的快照，不把未应用草稿的单位/点表套在旧值上。
/// 只读页面不会构建适配器、读设备、读取凭证或返回原始报文。字段按 key 分页，版本切换拒绝旧游标。</summary>
internal static class RuntimeDeviceEndpoints
{
    public static RouteGroupBuilder MapRuntimeDeviceEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/runtime/devices", List).WithTags("Runtime").WithName("getRuntimeDevices")
            .WithSummary("查看运行设备与来源状态，单列未应用设备");
        api.MapGet("/runtime/devices/{deviceKey}/inputs/{inputKey}/values", Values).WithTags("Runtime")
            .WithName("getRuntimeCurrentValues").WithSummary("分页查看运行快照的点位当前值及最后成功值");
        return api;
    }

    private static Results<Ok<RuntimeDeviceList>, ProblemHttpResult> List(RuntimeCoordinator coordinator,
        RuntimeVersionStore versions, DeviceDraftStore devices, HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        try
        {
            var runtime = coordinator.Runtime;
            var status = runtime.GetStatus();
            if (status.ActiveRevision is null && versions.ReadPointers().EffectiveRevision is not null)
            { return Unavailable(); }
            var catalog = Catalog(versions, status.ActiveRevision);
            var draft = devices.Read();
            var known = catalog.Devices.ToDictionary(device => device.Key, StringComparer.Ordinal);
            var result = new List<RuntimeDeviceInfo>();
            var units = status.Units.GroupBy(unit => unit.Id).ToDictionary(group => group.Key, group => group.Last());
            var sourceGroups = catalog.Sources.ToLookup(source => source.DeviceKey, StringComparer.Ordinal);
            var draftKeys = draft.Devices.Select(device => device.Key).ToHashSet(StringComparer.Ordinal);
            foreach (var device in catalog.Devices)
            {
                var sources = sourceGroups[device.Key].Select(source =>
                {
                    var unit = units.GetValueOrDefault(source.Id);
                    return new RuntimeDeviceSource(source.Id.KeySegments[1], source.Name, source.Id.Kind, source.Enabled,
                        source.PeriodMs, source.Fields.Length, unit?.State, unit?.Reads,
                        status.ActiveRevision is { } revision ? runtime.CurrentValues.LastReceived(source.Id, revision) : null);
                }).ToArray();
                result.Add(new(device.Key, device.Name, device.Kind, Health(device.Enabled, sources), false,
                    catalog.HasDraft && !draftKeys.Contains(device.Key), sources));
            }
            foreach (var device in draft.Devices.Where(device => !known.ContainsKey(device.Key)))
            { result.Add(new(device.Key, device.Name, device.Kind.ToString().ToLowerInvariant(), "notApplied", true, false, [])); }
            if (runtime.ActiveRevision != status.ActiveRevision)
            { return Changed(); }
            return TypedResults.Ok(new RuntimeDeviceList(status.ActiveRevision, result.OrderBy(device => device.Key, StringComparer.Ordinal).ToArray(), runtime.CurrentValues.Status));
        }
        catch (RuntimePlanDocumentException) { return Unavailable(); }
    }

    private static Results<Ok<RuntimeCurrentValues>, ProblemHttpResult> Values(string deviceKey, string inputKey,
        long revision, string? after, int? limit, RuntimeCoordinator coordinator, RuntimeVersionStore versions, HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!BusinessKeys.IsValid(deviceKey) || !BusinessKeys.IsValid(inputKey) || revision <= 0 ||
            after is not null && !BusinessKeys.IsValid(after) || limit is < 1 or > 100)
        { return ApiProblems.Problem(400, "runtime.invalidValuesQuery", "点位查询参数无效。"); }
        try
        {
            var runtime = coordinator.Runtime;
            var status = runtime.GetStatus();
            if (status.ActiveRevision != revision)
            { return Changed(); }
            var catalog = Catalog(versions, revision);
            var source = catalog.Sources.FirstOrDefault(item => item.DeviceKey == deviceKey && item.Id.KeySegments[1] == inputKey);
            if (source is null)
            { return ApiProblems.Problem(404, "runtime.sourceNotFound", "当前版本不存在该来源。"); }
            var unit = status.Units.LastOrDefault(unit => unit.Id == source.Id);
            var fields = source.Fields.OrderBy(field => field.Key, StringComparer.Ordinal)
                .Where(field => after is null || string.CompareOrdinal(field.Key, after) > 0).Take((limit ?? 100) + 1).ToArray();
            var page = fields.Take(limit ?? 100).ToArray();
            var values = runtime.CurrentValues.Read(source.Id, revision, page.Select(field => field.Key), source.PeriodMs,
                unit?.Reads?.Health is InputReadHealth.Failing or InputReadHealth.Offline, unit?.State == RuntimeUnitState.Running);
            if (runtime.ActiveRevision != revision)
            { return Changed(); }
            return TypedResults.Ok(new RuntimeCurrentValues(revision, deviceKey, inputKey,
                page.Select((field, index) => new RuntimePointValue(field.Key, field.Name, field.Unit, source.Enabled && field.Enabled,
                    source.Enabled && field.Enabled ? values[index] : new(field.Key, null, null, "disabled", null, false, null, null, null))).ToArray(),
                fields.Length > page.Length ? page[^1].Key : null, source.Fields.Length));
        }
        catch (RuntimePlanDocumentException) { return Unavailable(); }
    }

    internal static string Health(bool enabled, RuntimeDeviceSource[] sources)
    {
        if (!enabled)
        { return "disabled"; }
        var active = sources.Where(source => source.Enabled).ToArray();
        if (active.Length == 0)
        { return "inactive"; }
        // 推送设备不把连接成功或长期沉默冒充传感器在线/离线。
        if (active.All(source => source.Kind == "input.mqtt"))
        { return "push"; }
        if (active.All(source => source.Reads?.Health == InputReadHealth.Offline))
        { return "offline"; }
        if (active.All(source => source.State == RuntimeUnitState.Running && source.Reads?.Health == InputReadHealth.Normal))
        { return "normal"; }
        if (active.Any(source => source.Reads?.Health is InputReadHealth.PartialFailure or InputReadHealth.Failing or InputReadHealth.Offline ||
            source.State is not (null or RuntimeUnitState.Running)))
        { return "partialFailure"; }
        return "notRead";
    }

    private static (DeviceInfo[] Devices, SourceInfo[] Sources, bool HasDraft) Catalog(RuntimeVersionStore versions, long? revision)
    {
        if (revision is null)
        { return ([], [], false); }
        var snapshot = versions.Load(revision.Value) ?? throw new RuntimePlanDocumentException("运行快照不存在。");
        var (plan, draft) = RuntimeSnapshotDocument.Read(snapshot);
        var sources = new List<SourceInfo>();
        var devices = new List<DeviceInfo>();
        if (draft is not null)
        {
            var inputGroups = draft.Inputs.ToLookup(input => input.DeviceId);
            var scheduled = plan.Units.Select(unit => unit.Id).ToHashSet();
            foreach (var device in draft.Devices)
            {
                devices.Add(new(device.Key, device.Name, device.Kind.ToString().ToLowerInvariant(), device.Enabled));
                foreach (var input in inputGroups[device.Id])
                {
                    var id = RuntimeUnitId.Create("input." + device.Kind.ToString().ToLowerInvariant(), device.Key + "/" + input.Key);
                    // 启用但不在任何启用路线中的来源没有采集循环，不能标成已运行。
                    var enabled = device.Enabled && input.Enabled && scheduled.Contains(id);
                    sources.Add(new(device.Key, id, input.Name, enabled, input.Modbus?.PeriodMs ?? input.Http?.PeriodMs ?? input.Simulator?.PeriodMs,
                        Fields(input)));
                }
            }
        }
        else
        {
            // 兼容已交付的 T1/T2 种子快照；不把当前草稿当历史元数据。
            foreach (var unit in plan.Units.Where(unit => unit.Id.Kind.StartsWith("input.", StringComparison.Ordinal) && unit.Id.KeySegments.Count == 2))
            {
                var key = unit.Id.KeySegments[0];
                if (!devices.Any(device => device.Key == key))
                { devices.Add(new(key, key, unit.Id.Kind[6..], true)); }
                var (period, fields) = LegacyFields(unit);
                sources.Add(new(key, unit.Id, unit.Id.KeySegments[1], true, period, fields));
            }
        }
        return (devices.ToArray(), sources.ToArray(), draft is not null);
    }

    private static FieldInfo[] Fields(InputDraft input) => input.Kind switch
    {
        DeviceKind.Modbus => input.Modbus!.Points.Select(point => new FieldInfo(point.Key, point.Name, point.Unit, point.Enabled)).ToArray(),
        DeviceKind.Http => JsonFields(input.Http!.Fields),
        DeviceKind.Mqtt => JsonFields(input.Mqtt!.Fields),
        DeviceKind.Simulator => SimulatorSourceValidation.FieldKeys(input.Simulator!).Select(key => new FieldInfo(key, null, null, true)).ToArray(),
        _ => [],
    };
    private static FieldInfo[] JsonFields(JsonSourceField[] fields) => fields.Select(field => new FieldInfo(field.Key, field.Name, field.Unit, true)).ToArray();
    private static (int? Period, FieldInfo[] Fields) LegacyFields(RuntimeUnitPlan unit)
    {
        if (unit.Id.Kind == "input.modbus")
        {
            var config = ModbusInputSettings.Parse(unit.Configuration);
            return (config.PeriodMs, config.Points.Select(point => new FieldInfo(point.Key, point.Name, point.Unit, point.Enabled)).ToArray());
        }
        if (unit.Id.Kind == "input.simulator")
        {
            return (unit.Configuration.TryGetProperty("periodMs", out var period) ? period.GetInt32() : 5000,
                SimulatorInputFactory.Describe(unit, unit.Id.KeySegments[0], unit.Id.KeySegments[1]).Keys.Select(key => new FieldInfo(key, null, null, true)).ToArray());
        }
        var settings = unit.Configuration;
        return (unit.Id.Kind == "input.http" ? HttpInputSettings.Parse(settings).PeriodMs : null,
            settings.TryGetProperty("fields", out var fields) && fields.ValueKind == System.Text.Json.JsonValueKind.Array
                ? fields.EnumerateArray().Select(field => new FieldInfo(field.GetProperty("key").GetString()!, null, null, true)).ToArray() : []);
    }

    private static ProblemHttpResult Changed() => ApiProblems.Problem(409, "runtime.revisionChanged", "运行版本已切换，请重新选择来源。");
    private static ProblemHttpResult Unavailable() => ApiProblems.Problem(409, "runtime.snapshotUnavailable", "运行快照不可读取，不能用草稿替代。");
    private sealed record DeviceInfo(string Key, string Name, string Kind, bool Enabled);
    private sealed record SourceInfo(string DeviceKey, RuntimeUnitId Id, string Name, bool Enabled, int? PeriodMs, FieldInfo[] Fields);
    private sealed record FieldInfo(string Key, string? Name, string? Unit, bool Enabled);
}
