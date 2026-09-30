namespace TenonEdge.Host.Security;

/// <summary>
/// 请求的来源地址：来自受信任代理时为代理转发的客户端地址（T0-08），否则为实际连接的对端地址。
/// IPv4 映射的 IPv6 地址按 IPv4 显示。用于审计、首次设置记录与限流。
/// </summary>
internal static class SourceAddress
{
    public static string? Of(HttpContext context)
    {
        var address = context.Connection.RemoteIpAddress;
        if (address is null)
        {
            return null;
        }

        return (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();
    }
}
