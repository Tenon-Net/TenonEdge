using TenonEdge.Host.Features.Runtime;
using TenonEdge.Host.Storage;

namespace TenonEdge.UnitTests.Runtime;

/// <summary>
/// 启动保护的判定（T1-01；总设计 §7.4、§8.4，D-07）：只针对从未稳定的有效版本，累计 3 次非正常退出触发；
/// 上一生效版本曾经稳定才自动回滚一次，否则或回滚生成的版本再次触发时进入“运行失败”。
/// </summary>
public sealed class StartupProtectionTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(2, false)]
    [InlineData(3, true)] // 已稳定的版本上的退出不计入，即使计数达到上限也照常激活
    [InlineData(10, true)]
    public void VersionsBelowTheLimitOrAlreadyStableAreActivated(int exits, bool stable)
    {
        var decision = StartupProtection.Decide(Snapshot(2, exits, stable), Snapshot(1, 0, stable: true));

        Assert.Equal(StartupProtectionAction.Activate, decision.Action);
    }

    [Fact]
    public void ReachingTheLimitRollsBackOnceToAPreviouslyStableVersion()
    {
        var decision = StartupProtection.Decide(Snapshot(2, 3, stable: false), Snapshot(1, 0, stable: true));

        Assert.Equal(StartupProtectionAction.RollBack, decision.Action);
        Assert.Equal(1, decision.RollbackTarget);
        Assert.Contains("r2", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void APreviousVersionThatNeverBecameStableIsNotARollbackTarget()
    {
        var decision = StartupProtection.Decide(Snapshot(2, 3, stable: false), Snapshot(1, 0, stable: false));

        Assert.Equal(StartupProtectionAction.RunFailed, decision.Action);
        Assert.Contains("r1 从未稳定运行", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutAPreviousVersionTheRuntimeFails()
    {
        var decision = StartupProtection.Decide(Snapshot(1, 3, stable: false), previous: null);

        Assert.Equal(StartupProtectionAction.RunFailed, decision.Action);
        Assert.Contains("没有上一生效版本", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AVersionCreatedByAutomaticRollbackIsNotRolledBackAgain()
    {
        var rolledBack = Snapshot(3, 3, stable: false, origin: RuntimeSnapshotOrigins.AutoRollback);

        var decision = StartupProtection.Decide(rolledBack, Snapshot(2, 3, stable: false));

        Assert.Equal(StartupProtectionAction.RunFailed, decision.Action);
        Assert.Contains("自动回滚生成的版本", decision.Reason, StringComparison.Ordinal);
    }

    private static RuntimeSnapshotRecord Snapshot(long revision, int exits, bool stable, string origin = RuntimeSnapshotOrigins.Seed) => new()
    {
        Revision = revision,
        AbnormalExitCount = exits,
        IsStable = stable,
        Origin = origin,
        State = RuntimeSnapshotStates.Effective,
    };
}
