using TenonEdge.Abstractions.Parameters;

namespace TenonEdge.Adapters.Mqtt;

public static class MqttConnectionParameters
{
    public static ParameterSchema Schema { get; } = new("connection.mqtt", ParameterScope.Connection,
    [
        new("host", "主机或 IP", ParameterType.Text) { Required = true, MaximumLength = 253, Group = "目标" },
        new("port", "端口", ParameterType.Number)
        { Required = true, WholeNumber = true, DefaultJson = "1883", MinimumJson = "1", MaximumJson = "65535", Group = "目标" },
        new("tls", "启用 TLS", ParameterType.Switch) { Required = true, DefaultJson = "false", Group = "目标" },
        new("clientId", "ClientId（留空自动生成）", ParameterType.Text) { MaximumLength = 128, Group = "会话" },
        new("timeoutMs", "总超时（毫秒）", ParameterType.Number)
        { Required = true, WholeNumber = true, DefaultJson = "5000", MinimumJson = "100", MaximumJson = "60000", Group = "时限" },
        new("pingIntervalMs", "保活 / PING 间隔（毫秒）", ParameterType.Number)
        { Required = true, WholeNumber = true, DefaultJson = "15000", MinimumJson = "1000", MaximumJson = "60000", Group = "时限" },
    ]);
}
