using TenonEdge.Abstractions.Parameters;

namespace TenonEdge.Adapters.Modbus;

/// <summary>连接表单和正式适配器共同使用的标量声明；主机语义与出站策略仍由原实现负责。</summary>
public static class ModbusConnectionParameters
{
    public static ParameterSchema Schema { get; } = new("connection.modbus", ParameterScope.Connection,
    [
        new("host", "主机或 IP", ParameterType.Text) { Required = true, MaximumLength = 253, Group = "目标" },
        Number("port", "端口", "502", "1", "65535", "目标"),
        new("format", "帧格式", ParameterType.Choice)
        {
            Required = true, DefaultJson = "\"tcp\"", Group = "协议",
            Options = [new("tcp", "Modbus TCP"), new("rtuOverTcp", "RTU over TCP")],
        },
        Number("connectTimeoutMs", "连接超时（毫秒）", "5000", "100", "60000", "时限"),
        Number("readTimeoutMs", "读取超时（毫秒）", "1000", "100", "60000", "时限"),
        Number("requestIntervalMs", "请求间隔（毫秒）", "0", "0", "60000", "时限"),
        Number("rtuSilenceMs", "RTU 静默间隔（毫秒）", "500", "1", "60000", "时限"),
    ]);

    private static ParameterDefinition Number(string name, string label, string value, string min, string max, string group) =>
        new(name, label, ParameterType.Number)
        { Required = true, WholeNumber = true, DefaultJson = value, MinimumJson = min, MaximumJson = max, Group = group };
}
