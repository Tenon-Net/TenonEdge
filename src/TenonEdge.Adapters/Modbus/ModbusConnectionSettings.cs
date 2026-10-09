using System.Text.Json;

namespace TenonEdge.Adapters.Modbus;

/// <summary>物理连接参数，不含 UnitId；同一连接的采集组必须使用相同参数。</summary>
public sealed record ModbusConnectionSettings
{
    public long ConnectionId { get; init; }
    public string Host { get; init; } = string.Empty;
    public int Port { get; init; } = 502;
    public ModbusFrameFormat Format { get; init; }
    public int ConnectTimeoutMs { get; init; } = 5000;
    public int ReadTimeoutMs { get; init; } = 1000;
    public int RequestIntervalMs { get; init; }
    public int RtuSilenceMs { get; init; } = 500;

    public ModbusConnectionSettings Validate()
    {
        if (ConnectionId is < 1 or > 9007199254740991L)
        { throw new ArgumentException("Modbus 连接编号无效。"); }
        if (string.IsNullOrWhiteSpace(Host) || Host.Length > 253 || Host != Host.Trim() ||
            Uri.CheckHostName(Host) == UriHostNameType.Unknown)
        { throw new ArgumentException("Modbus 主机必须为固定主机名或 IP 地址。"); }
        if (Port is < 1 or > 65535 || !Enum.IsDefined(Format))
        { throw new ArgumentException("Modbus 端口或帧格式无效。"); }
        if (ConnectTimeoutMs is < 100 or > 60000 || ReadTimeoutMs is < 100 or > 60000 ||
            RequestIntervalMs is < 0 or > 60000 || RtuSilenceMs is < 1 or > 60000)
        { throw new ArgumentException("Modbus 超时、请求间隔或静默间隔无效。"); }
        return this with { Host = Host.ToLowerInvariant() };
    }

    public string Identity => JsonSerializer.Serialize(new
    { Host, Port, Format, ConnectTimeoutMs, ReadTimeoutMs, RequestIntervalMs, RtuSilenceMs });
}
