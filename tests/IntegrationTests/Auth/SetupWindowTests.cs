using TenonEdge.Host.Features.Auth;
using TenonEdge.IntegrationTests.Infrastructure;

namespace TenonEdge.IntegrationTests.Auth;

/// <summary>
/// 首次设置窗口按单调计时计算（T0-07；总设计 §10.1、§10.3）：开机后系统时钟被校正时，窗口既不会立刻关闭，也不会被延长。
/// </summary>
public sealed class SetupWindowTests
{
    private static readonly DateTimeOffset BootClock = new(1970, 1, 1, 0, 0, 30, TimeSpan.Zero);

    [Fact]
    public void ForwardClockCorrectionAfterBootDoesNotCloseTheWindow()
    {
        var time = new ManualTimeProvider(BootClock);
        var window = new SetupWindow(time);
        window.Open();

        // 没有实时时钟的设备以 1970 年开机，启动后经 NTP 校正到当前时间。
        time.StepWallClock(new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero) - BootClock);

        Assert.True(window.IsOpen);
        Assert.Equal(time.GetUtcNow() + SetupWindow.Duration, window.ClosesAt);
        time.Advance(SetupWindow.Duration - TimeSpan.FromSeconds(1));
        Assert.True(window.IsOpen);
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.False(window.IsOpen);
    }

    [Fact]
    public void BackwardClockCorrectionDoesNotExtendTheWindow()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2027, 6, 1, 8, 0, 0, TimeSpan.Zero));
        var window = new SetupWindow(time);
        window.Open();
        time.Advance(TimeSpan.FromMinutes(20));

        // 实时时钟走快，启动后被校正回退 90 天：窗口仍在开放后 30 分钟关闭。
        time.StepWallClock(-TimeSpan.FromDays(90));

        Assert.Equal(time.GetUtcNow() + TimeSpan.FromMinutes(10), window.ClosesAt);
        time.Advance(TimeSpan.FromMinutes(10));
        Assert.False(window.IsOpen);
    }
}
