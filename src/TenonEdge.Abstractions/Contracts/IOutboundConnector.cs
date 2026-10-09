using System.Net;

namespace TenonEdge.Abstractions.Contracts;

/// <summary>每次建立连接时按当前规则检查实际 IP，不接受仅在保存配置时校验过的域名。</summary>
public interface IOutboundAddressPolicy
{
    bool IsAllowed(IPAddress address, int port);
}

/// <summary>解析、校验并连接同一个 IP；不使用系统代理。调用方负责总超时，并释放返回的流。</summary>
public interface IOutboundConnector
{
    ValueTask<Stream> ConnectAsync(string host, int port, CancellationToken cancellationToken);

    /// <summary>按调用观察解析/建连边界，不含主机、地址或秘密。未提供诊断的实现不报告阶段，不能据此伪造分阶段成功。</summary>
    ValueTask<Stream> ConnectAsync(string host, int port, Action<OutboundConnectionStage> progress, CancellationToken cancellationToken) =>
        ConnectAsync(host, port, cancellationToken);
}

public enum OutboundConnectionStage { Resolving, Connecting }

/// <summary>解析出的目标均不在当前出站允许范围，不能当作协议数据错误重试。</summary>
public sealed class OutboundAccessDeniedException : IOException
{
    public OutboundAccessDeniedException() : base("目标不在允许的出站地址与端口范围内。") { }
}
