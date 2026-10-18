using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using TenonEdge.Abstractions.Contracts;
using TenonEdge.Abstractions.Plans;
using TenonEdge.Adapters.Http;
using TenonEdge.Adapters.Modbus;
using TenonEdge.Adapters.Mqtt;
using TenonEdge.Host.Features.Connections;
using TenonEdge.Runtime.Inputs;
using TenonEdge.Runtime.Outputs;

namespace TenonEdge.Host.Features.Runtime;

/// <summary>冻结连接和协议委托的纯结构读取；历史保护、配额、身份与凭证核对不执行模块。</summary>
internal static class RuntimeConnectionReferences
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        PropertyNameCaseInsensitive = false,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };
    private static readonly HashSet<string> MqttProperties = new(StringComparer.Ordinal)
        { "host", "port", "tls", "clientId", "timeoutMs", "pingIntervalMs", "usePassword" };

    internal static RuntimeUnitPlan Protocol(RuntimeUnitPlan unit) => InputModuleCatalog.ReadProtocol(unit) ?? OutputModuleCatalog.ReadProtocol(unit) ?? unit;

    private static IEnumerable<ModuleConnectionSnapshot> ModuleConnections(RuntimeUnitPlan unit) =>
        InputModuleCatalog.ReadConnections(unit).Values.Concat(OutputModuleCatalog.ReadConnections(unit).Values);

    internal static IEnumerable<long> ReadIds(RuntimeUnitPlan unit)
    {
        if (unit.Configuration.TryGetProperty("connectionId", out var value))
        { yield return value.Deserialize<long>(JsonSerializerOptions.Web); }
        foreach (var connection in ModuleConnections(unit))
        { yield return connection.ConnectionId; }
    }

    internal static void ValidateModuleConnections(RuntimeUnitPlan unit)
    {
        foreach (var connection in ModuleConnections(unit))
        {
            switch (connection.Kind)
            {
                case "http":
                    _ = Http(connection);
                    break;
                case "mqtt":
                    _ = Mqtt(connection);
                    break;
                case "modbus":
                    _ = Modbus(connection);
                    break;
                default:
                    throw new ArgumentException("输入连接协议不受支持。");
            }
        }
    }

    internal static IEnumerable<HttpRequestSettings> ReadModuleHttp(RuntimeUnitPlan unit) =>
        ModuleConnections(unit).Where(connection => connection.Kind == "http").Select(Http);

    internal static IEnumerable<HttpRequestSettings> ReadHttp(RuntimeUnitPlan unit)
    {
        var protocol = Protocol(unit);
        if (protocol.Id.Kind == "input.http")
        { yield return HttpInputSettings.Parse(protocol.Configuration); }
        else if (protocol.Id.Kind == "output.http")
        { yield return HttpOutputSettings.Parse(protocol.Configuration); }
        foreach (var connection in ReadModuleHttp(unit))
        { yield return connection; }
    }

    internal static IEnumerable<MqttConnectionSettings> ReadMqtt(RuntimeUnitPlan unit)
    {
        var protocol = Protocol(unit);
        if (protocol.Id.Kind == "input.mqtt")
        { yield return MqttInputSettings.Parse(protocol.Configuration); }
        else if (protocol.Id.Kind == "output.mqtt")
        { yield return MqttOutputSettings.Parse(protocol.Configuration); }
        foreach (var connection in ModuleConnections(unit).Where(connection => connection.Kind == "mqtt"))
        { yield return Mqtt(connection); }
    }

    internal static IEnumerable<ModbusConnectionSettings> ReadModbus(RuntimeUnitPlan unit)
    {
        var protocol = Protocol(unit);
        if (protocol.Id.Kind == "input.modbus")
        { yield return ModbusInputSettings.Parse(protocol.Configuration).ToConnectionSettings(); }
        foreach (var connection in ModuleConnections(unit).Where(connection => connection.Kind == "modbus"))
        { yield return Modbus(connection); }
    }

    private static HttpRequestSettings Http(ModuleConnectionSnapshot connection)
    {
        var value = connection.Configuration.Deserialize<HttpConnectionDraft>(Json) ?? throw new ArgumentException("输入连接快照无效。");
        var validated = ConnectionDraftValidation.Normalize(connection.ConnectionId,
            new("frozen", "frozen", "frozen", ConnectionKind.Http, Http: value)).Http!;
        return new HttpRequestSettings
        {
            ConnectionId = connection.ConnectionId,
            BaseAddress = validated.BaseAddress,
            TimeoutMs = validated.TimeoutMs,
            Headers = (validated.Headers ?? []).ToImmutableDictionary(StringComparer.OrdinalIgnoreCase),
        };
    }

    private static MqttConnectionSettings Mqtt(ModuleConnectionSnapshot connection)
    {
        if (connection.Configuration.EnumerateObject().Any(property => !MqttProperties.Contains(property.Name)))
        { throw new ArgumentException("MQTT 连接快照包含未知字段。"); }
        var value = connection.Configuration.Deserialize<MqttConnectionSettings>(Json) ?? throw new ArgumentException("输入连接快照无效。");
        return (value with { ConnectionId = connection.ConnectionId }).ValidateConnection();
    }

    private static ModbusConnectionSettings Modbus(ModuleConnectionSnapshot connection)
    {
        var value = connection.Configuration.Deserialize<ModbusConnectionDraft>(Json) ?? throw new ArgumentException("输入连接快照无效。");
        return new ModbusConnectionSettings
        {
            ConnectionId = connection.ConnectionId,
            Host = value.Host,
            Port = value.Port,
            Format = value.Format,
            ConnectTimeoutMs = value.ConnectTimeoutMs,
            ReadTimeoutMs = value.ReadTimeoutMs,
            RequestIntervalMs = value.RequestIntervalMs,
            RtuSilenceMs = value.RtuSilenceMs,
        }.Validate();
    }
}
