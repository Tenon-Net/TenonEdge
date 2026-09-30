namespace TenonEdge.Host.Features.Auth;

/// <summary>
/// 首次设置窗口（总设计 §10.3）：服务启动后开放 30 分钟，过期关闭；状态不是“已初始化”时，重启服务后重新开放。
/// 窗口长度按单调计时（<see cref="TimeProvider.GetTimestamp"/>）计算：边缘设备开机后系统时钟可能被校正（总设计 §10.1），
/// 按系统时钟计算时，时钟向前跳会使窗口立刻关闭，向后跳会使窗口延长。关闭时间按当前时钟换算后显示。
/// 计时使用注入的 TimeProvider，测试可以控制窗口到期。
/// </summary>
internal sealed class SetupWindow(TimeProvider time)
{
    public static readonly TimeSpan Duration = TimeSpan.FromMinutes(30);

    private long? _openedAt;

    /// <summary>窗口关闭的时间：当前时间加上剩余时长。</summary>
    public DateTimeOffset ClosesAt => time.GetUtcNow() + Duration - Elapsed(
        _openedAt ?? throw new InvalidOperationException("设置窗口尚未随服务启动开放。"));

    public bool IsOpen => _openedAt is { } openedAt && Elapsed(openedAt) < Duration;

    /// <summary>服务启动时调用一次，开始计时。</summary>
    public void Open() => _openedAt = time.GetTimestamp();

    private TimeSpan Elapsed(long openedAt) => time.GetElapsedTime(openedAt);
}
