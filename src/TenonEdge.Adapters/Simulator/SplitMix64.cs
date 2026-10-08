namespace TenonEdge.Adapters.Simulator;

/// <summary>
/// 可复现的伪随机数（SplitMix64，Steele、Lea、Flood 2014 公布的算法，常数取自其公开描述）：同一种子在任何平台、任何 .NET 版本上
/// 得到同一序列。<see cref="Random"/> 不保证跨版本的序列不变，因此模拟输入不用它。不用于任何安全用途。
/// </summary>
internal struct SplitMix64(ulong seed)
{
    private ulong _state = seed;

    public ulong NextUInt64()
    {
        unchecked
        {
            _state += 0x9E3779B97F4A7C15UL;
            var z = _state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }

    /// <summary>[0, 1) 上的均匀分布，取高 53 位。</summary>
    public double NextDouble() => (NextUInt64() >> 11) * (1.0 / (1UL << 53));

    /// <summary>[0, <paramref name="exclusiveMaximum"/>) 中的整数。</summary>
    public int NextInt32(int exclusiveMaximum)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(exclusiveMaximum, 1);
        return (int)(NextDouble() * exclusiveMaximum);
    }
}
