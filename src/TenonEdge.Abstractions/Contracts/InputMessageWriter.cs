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

    /// <summary>不可重投输入的非等待入口。先检查通道与预算，再同步解析；false 表示容量不足且未转交消息。
    /// true 仅表示已入内存通道（或解析器明确返回 null），不表示持久提交。单写者调用，不另建缓冲。</summary>
    public abstract bool TryProduce(long maximumWorkingBytes, long maximumMessageBytes,
        Func<EdgeMessage?> produce, CancellationToken cancellationToken = default);

    /// <summary>面向可重投输入：等待持久接管或明确拒绝，不以进入内存通道为成功。
    /// 工厂须声明 UsesDurableHandoff。取消只结束调用方等待，已入通道的数据仍由运行时持有；取消或失败不能确认上游。
    /// 回调返回 null 表示已由输入分类/计数的明确丢弃。返回 Committed/Rejected 才允许按输入到达顺序确认。
    /// </summary>
    public abstract ValueTask<InputHandoffStatus> ProduceConfirmedAsync(long maximumWorkingBytes, long maximumMessageBytes,
        Func<CancellationToken, ValueTask<EdgeMessage?>> produce, CancellationToken cancellationToken = default);

    public abstract ValueTask ProduceAsync(long maximumBytes,
        Func<CancellationToken, ValueTask<EdgeMessage?>> produce, CancellationToken cancellationToken = default);
}

/// <summary>输入声明的大小超过总额度，或实际消息超过声明值；属于数据/配置错误，不应重启运行单元。</summary>
public sealed class InputMessageSizeException(string message) : ArgumentException(message);

/// <summary>确认面向整条源消息：Committed 可包含已明确按策略拒绝的分支；StorageFailed/Stopped 均不允许确认。</summary>
// Buffered 仅用于 QoS 0 已入内存通道，绝不能据此确认 QoS 1。
public enum InputHandoffStatus { Committed, Rejected, StorageFailed, Stopped, Buffered }
