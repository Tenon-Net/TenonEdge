using TenonEdge.Adapters.Http;
using TenonEdge.Adapters.Modbus;
using TenonEdge.Adapters.Mqtt;
using TenonEdge.Host.Features.Runtime;

namespace TenonEdge.Host.Features.Routes;

/// <summary>新配置的准入策略。只收紧既有安全上界，不改变已保存快照的格式/哈希或历史恢复能力。</summary>
internal sealed record RouteCompilationLimits(int Sources = 2000, int Connections = 500, int ModbusConnections = 256,
    int PathsPerSource = 32, int TotalPaths = 20000)
{
    public static RouteCompilationLimits Default { get; } = new();

    public void Validate()
    {
        if (Sources is < 1 or > 2000 || Connections is < 1 or > 500 || ModbusConnections is < 1 or > 256 ||
            PathsPerSource is < 1 or > 32 || TotalPaths is < 1 or > 20000)
        { throw new ArgumentException("计划规模上限：来源1—2000、连接1—500、Modbus连接1—256、每来源路径1—32、总路径1—20000；仍须在本机实测，不放宽既有安全上界。"); }
    }

    // 冻结/COMMIT 复核已编译计划，不重新解释草稿或展开图。queue-only不算数据路径，但占连接数。
    public void Check(RuntimePlanDocument plan)
    {
        Validate();
        var inputs = 0;
        var paths = new Dictionary<string, int>(StringComparer.Ordinal);
        var total = 0;
        var connections = new HashSet<long>();
        var modbus = new HashSet<long>();
        foreach (var unit in plan.Units)
        {
            if (unit.Id.Kind.StartsWith("input.", StringComparison.Ordinal) && ++inputs > Sources)
            { Reject("sources.totalLimit"); }
            IEnumerable<string> sources = [];
            foreach (var id in RuntimeConnectionReferences.ReadIds(unit))
            { connections.Add(id); }
            foreach (var reference in RuntimeConnectionReferences.ReadModbus(unit))
            { modbus.Add(reference.ConnectionId); }
            switch (unit.Id.Kind)
            {
                case "input.modbus":
                    var modbusId = ModbusInputSettings.Parse(unit.Configuration).ConnectionId;
                    modbus.Add(modbusId);
                    connections.Add(modbusId);
                    break;
                case "input.http":
                    connections.Add(HttpInputSettings.Parse(unit.Configuration).ConnectionId);
                    break;
                case "input.mqtt":
                    connections.Add(MqttInputSettings.Parse(unit.Configuration).ConnectionId);
                    break;
                case "output.http":
                    var http = HttpOutputSettings.Parse(unit.Configuration);
                    connections.Add(http.ConnectionId);
                    if (!http.QueueOnly)
                    { sources = http.Inputs; }
                    break;
                case "output.mqtt":
                    var mqtt = MqttOutputSettings.Parse(unit.Configuration);
                    connections.Add(mqtt.ConnectionId);
                    if (!mqtt.QueueOnly)
                    { sources = mqtt.Inputs; }
                    break;
            }
            if (connections.Count > Connections)
            { Reject("connections.totalLimit"); }
            if (modbus.Count > ModbusConnections)
            { Reject("connections.modbusLimit"); }
            foreach (var source in sources)
            {
                var count = paths.GetValueOrDefault(source) + 1;
                if (count > PathsPerSource)
                { Reject("paths.sourceLimit"); }
                paths[source] = count;
                if (++total > TotalPaths)
                { Reject("paths.totalLimit"); }
            }
        }
    }

    private static void Reject(string code) => throw new RouteTopologyException(new(code, ""));
}
