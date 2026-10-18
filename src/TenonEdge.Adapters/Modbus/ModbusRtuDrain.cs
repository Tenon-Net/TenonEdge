namespace TenonEdge.Adapters.Modbus;

internal readonly record struct ModbusRtuDrainResult(bool CanReuseConnection, long DiscardedBytes, bool ConnectionClosed = false);

/// <summary>读取超时后的 RTU 接收隔离；调用时不得有其他读取操作，返回前不发送下一个请求。
/// 为保持其他从站额外等待不超过一次静默间隔，不因新字节无限延长窗口。
/// 只有完整窗口未见字节才能复用；窗口中见到迟到字节时丢弃，并要求会话立即换连接而非退避。
/// 本函数不关闭/创建连接，不修改任何从站的离线状态。</summary>
internal static class ModbusRtuDrain
{
    internal static async Task<ModbusRtuDrainResult> DrainAsync(Stream stream, TimeSpan silence,
        TimeProvider time, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(time);
        if (silence <= TimeSpan.Zero || silence > TimeSpan.FromMilliseconds(uint.MaxValue - 1))
        { throw new ArgumentOutOfRangeException(nameof(silence)); }
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = new CancellationTokenSource(silence, time);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var buffer = new byte[256];
        long discarded = 0;
        try
        {
            while (true)
            {
                operation.Token.ThrowIfCancellationRequested();
                var count = await stream.ReadAsync(buffer, operation.Token);
                cancellationToken.ThrowIfCancellationRequested();
                if (count == 0)
                { return new(false, discarded, ConnectionClosed: true); }
                discarded += count;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            // ReadAsync 已实际退出，才能让调用者复用/关闭句柄；不使用 WhenAny 后遗留未完成读取。
            return new(discarded == 0, discarded);
        }
    }
}
