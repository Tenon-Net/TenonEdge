namespace TenonEdge.Abstractions.Contracts;

/// <summary>
/// 协议解码之前的原始输入缓冲额度。与解析/分发共享进程总上限，但有独立分区，不能占尽解析额度。
/// 先预约再分配；null 表示当前不能接收，调用方按协议丢弃，或限时等待后不确认断连，不另建报文等待队列。
/// 租约覆盖读取、排队及回调持有原始数据的全部时间；取消/断连本身不代表数据已不再被引用。
/// Dispose 可重复且线程安全，只有最后一个实际持有者可以释放，不随请求作用域自动释放。
/// </summary>
public interface IInputBufferBudget
{
    IDisposable? TryReserveBuffer(long bytes);

    /// <summary>可重投输入的限时等待入口；调用方必须提供期限，取消不遗留预约。</summary>
    ValueTask<IDisposable> ReserveBufferAsync(long bytes, CancellationToken cancellationToken);
}
