namespace TenonEdge.Runtime.Lifecycle;

/// <summary>
/// 运行单元故障后的重启等待（总设计 §8.1“按退避重启”、§8.4“最高 60 秒，不紧密循环”）：
/// 第 n 次连续故障的基准等待为 初始等待 × 2^(n−1)，不超过上限；再按抖动比例随机缩短，结果落在 (基准 × (1 − 抖动), 基准]。
/// </summary>
internal sealed class RestartBackoff
{
    private readonly TimeSpan _initial;
    private readonly TimeSpan _maximum;
    private readonly double _jitter;
    private readonly Func<double> _random;

    /// <param name="random">返回 [0, 1) 的随机数；测试中可以固定。</param>
    public RestartBackoff(TimeSpan initial, TimeSpan maximum, double jitter, Func<double> random)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(initial, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximum, initial);
        ArgumentNullException.ThrowIfNull(random);
        _initial = initial;
        _maximum = maximum;
        _jitter = jitter;
        _random = random;
    }

    /// <summary>连续运行满这一时长后，下一次故障的退避从头计算。</summary>
    public TimeSpan ResetAfter => _maximum;

    /// <param name="consecutiveFailures">连续故障次数，从 1 开始。</param>
    public TimeSpan Delay(int consecutiveFailures)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(consecutiveFailures, 1);

        // 2^30 倍的初始等待已远超任何合理上限，指数封顶避免溢出。
        var exponent = Math.Min(consecutiveFailures - 1, 30);
        var ticks = Math.Min((double)_initial.Ticks * Math.Pow(2, exponent), _maximum.Ticks);
        var jittered = ticks * (1 - (_jitter * _random()));
        return TimeSpan.FromTicks(Math.Max(1, (long)jittered));
    }
}
