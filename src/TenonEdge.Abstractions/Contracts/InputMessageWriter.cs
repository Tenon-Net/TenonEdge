using System.Threading.Channels;
using TenonEdge.Abstractions.Messages;

namespace TenonEdge.Abstractions.Contracts;

/// <summary>
/// 输入发布入口。适配器使用 ProduceAsync：先取得全局额度，再读取/解析；回调返回前释放临时读取缓冲。
/// maximumBytes 必须覆盖读取、解析和消息构造期间的峰值；返回 null 表示本次没有消息。
/// 继承的直接写入方法用于已有消息的通道操作，不覆盖调用前的消息分配。
/// </summary>
public abstract class InputMessageWriter : ChannelWriter<EdgeMessage>
{
    /// <summary>读取/解析暂存与结果消息不同上限；两阶段不同时存活，预算取解析峰值与输出阶段预约的较大者。保守结果上界可令输出阶段预约总额度，实际结果仍须通过总额度校验。</summary>
    public abstract ValueTask ProduceAsync(long maximumWorkingBytes, long maximumMessageBytes,
        Func<CancellationToken, ValueTask<EdgeMessage?>> produce, CancellationToken cancellationToken = default);

    public abstract ValueTask ProduceAsync(long maximumBytes,
        Func<CancellationToken, ValueTask<EdgeMessage?>> produce, CancellationToken cancellationToken = default);
}

/// <summary>输入声明的大小超过总额度，或实际消息超过声明值；属于数据/配置错误，不应重启运行单元。</summary>
public sealed class InputMessageSizeException(string message) : ArgumentException(message);
