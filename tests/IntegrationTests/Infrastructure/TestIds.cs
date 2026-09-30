using TenonEdge.Host.Storage.Ids;

namespace TenonEdge.IntegrationTests.Infrastructure;

/// <summary>按给定的逻辑时间构造雪花 ID（机器号与序列取给定的低位），用于直接写库的存储测试。</summary>
internal static class TestIds
{
    public static long At(DateTimeOffset time, long lowBits = 0) =>
        (((time - SnowflakeIdGenerator.Epoch).Ticks / TimeSpan.TicksPerMillisecond) << SnowflakeIdGenerator.TimestampShift) | lowBits;
}
