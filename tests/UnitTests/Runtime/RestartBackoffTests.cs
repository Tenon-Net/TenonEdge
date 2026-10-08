using TenonEdge.Runtime.Lifecycle;

namespace TenonEdge.UnitTests.Runtime;

/// <summary>
/// 运行单元的重启退避（T1-01；总设计 §8.1、§8.4）：从初始等待开始按 2 倍递增，不超过 60 秒；抖动只缩短等待，不超过上限。
/// </summary>
public sealed class RestartBackoffTests
{
    [Fact]
    public void DelaysDoubleUpToTheMaximum()
    {
        var backoff = new RestartBackoff(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60), jitter: 0, random: () => 0.5);

        Assert.Equal(
            [1, 2, 4, 8, 16, 32, 60, 60],
            Enumerable.Range(1, 8).Select(failures => backoff.Delay(failures).TotalSeconds));
        Assert.Equal(TimeSpan.FromSeconds(60), backoff.Delay(int.MaxValue));
        Assert.Equal(TimeSpan.FromSeconds(60), backoff.ResetAfter);
    }

    [Theory]
    [InlineData(0.0, 60.0)]
    [InlineData(0.5, 57.0)]
    [InlineData(0.999999, 54.0)]
    public void JitterOnlyShortensTheDelay(double random, double expectedSeconds)
    {
        var backoff = new RestartBackoff(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60), jitter: 0.1, random: () => random);

        Assert.Equal(expectedSeconds, backoff.Delay(10).TotalSeconds, precision: 3);
    }

    [Fact]
    public void ConsecutiveFailuresStartAtOne() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new RestartBackoff(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60), 0, () => 0).Delay(0));
}
