namespace TenonEdge.Runtime.Lifecycle;

/// <summary>
/// 运行时的内部参数。默认值取自总设计 §8.4、§8.7，是待实测调整的初始值，不是容量承诺；
/// 管理员可调整的运行参数随 T1-07、T3 补充，本批只由宿主装配时设定。
/// </summary>
public sealed class RuntimeOptions
{
    /// <summary>每个运行单元停止时等待其循环退出的期限；到期仍未退出的单元标为“停止超时”。</summary>
    public TimeSpan UnitStopTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>单元故障后第一次重启前的等待；之后按 2 倍递增。</summary>
    public TimeSpan InitialRestartDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>重启等待的上限（总设计 §8.4“最高 60 秒”）。单元连续运行满这一时长后，退避从头计算。</summary>
    public TimeSpan MaximumRestartDelay { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>重启等待的随机缩短比例（0—1），使同时故障的单元错开重启；测试中可设为 0 以得到确定的时间。</summary>
    public double RestartJitter { get; init; } = 0.1;

    /// <summary>进程共享的输入/分发在途字节预算（总设计 §8.7）。</summary>
    public long InFlightBytes { get; init; } = 64L * 1024 * 1024;

    /// <summary>每个输入通道的容量（总设计 §8.4：每个输入 100 条）。</summary>
    public int InputChannelCapacity { get; init; } = 100;

    /// <summary>检查参数，不合理时抛出 <see cref="ArgumentOutOfRangeException"/>。</summary>
    public RuntimeOptions Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(UnitStopTimeout, TimeSpan.Zero, nameof(UnitStopTimeout));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(InitialRestartDelay, TimeSpan.Zero, nameof(InitialRestartDelay));
        ArgumentOutOfRangeException.ThrowIfLessThan(MaximumRestartDelay, InitialRestartDelay, nameof(MaximumRestartDelay));
        if (double.IsNaN(RestartJitter) || RestartJitter is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(RestartJitter), RestartJitter, "抖动比例应在 0 到 1 之间。");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(InputChannelCapacity, 1, nameof(InputChannelCapacity));
        ArgumentOutOfRangeException.ThrowIfLessThan(InFlightBytes, 1);
        return this;
    }
}
