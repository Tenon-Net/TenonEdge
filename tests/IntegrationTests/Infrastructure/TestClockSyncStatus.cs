using TenonEdge.Abstractions.Contracts;

namespace TenonEdge.IntegrationTests.Infrastructure;

/// <summary>显式控制校时状态，测试不依赖运行机器或容器的 NTP 配置。</summary>
internal sealed class TestClockSyncStatus : IClockSyncStatus
{
    public ClockSyncState State { get; set; } = ClockSyncState.Synchronized;
}
