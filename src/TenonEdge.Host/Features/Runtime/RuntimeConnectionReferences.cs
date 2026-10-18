using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using TenonEdge.Abstractions.Plans;
using TenonEdge.Adapters.Http;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Runtime.Inputs;

namespace TenonEdge.Host.Features.Runtime;

/// <summary>冻结计划的连接引用读取。历史保护、容量和运行准入使用同一结构，不依赖模块仍已安装。
/// 当前接通 HTTP；MQTT/Modbus 须完成原计划级绑定后再开放，不能旁路订阅/从站协调。</summary>
internal static class RuntimeConnectionReferences
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, PropertyNameCaseInsensitive = false };

    internal static IEnumerable<long> ReadIds(RuntimeUnitPlan unit)
    {
        if (unit.Configuration.TryGetProperty("connectionId", out var value))
        { yield return value.Deserialize<long>(JsonSerializerOptions.Web); }
        foreach (var connection in InputModuleCatalog.ReadConnections(unit).Values)
        { yield return connection.ConnectionId; }
    }

    internal static IEnumerable<HttpRequestSettings> ReadModuleHttp(RuntimeUnitPlan unit)
    {
        foreach (var connection in InputModuleCatalog.ReadConnections(unit).Values)
        {
            if (connection.Kind != "http")
            { throw new ArgumentException("此模块连接类型尚未接通原计划级协调。"); }
            var value = connection.Configuration.Deserialize<HttpConnectionDraft>(Json)
                ?? throw new ArgumentException("输入连接快照无效。");
            var validated = ConnectionDraftValidation.Normalize(connection.ConnectionId,
                new("frozen", "frozen", "frozen", ConnectionKind.Http, Http: value)).Http!;
            yield return new HttpRequestSettings
            {
                ConnectionId = connection.ConnectionId,
                BaseAddress = validated.BaseAddress,
                TimeoutMs = validated.TimeoutMs,
                Headers = (validated.Headers ?? []).ToImmutableDictionary(StringComparer.OrdinalIgnoreCase),
            };
        }
    }

    internal static IEnumerable<HttpRequestSettings> ReadHttp(RuntimeUnitPlan unit)
    {
        if (unit.Id.Kind == "input.http")
        { yield return HttpInputSettings.Parse(unit.Configuration); }
        else if (unit.Id.Kind == "output.http")
        { yield return HttpOutputSettings.Parse(unit.Configuration); }
        foreach (var connection in ReadModuleHttp(unit))
        { yield return connection; }
    }
}
