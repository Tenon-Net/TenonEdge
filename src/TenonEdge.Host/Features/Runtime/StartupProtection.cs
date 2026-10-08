using TenonEdge.Host.Storage;

namespace TenonEdge.Host.Features.Runtime;

/// <summary>
/// 启动保护的判定（总设计 §7.4，D-07 已于 2026-10-01 确认）：只针对从未达到稳定运行的有效版本，只统计非正常退出。
/// <list type="bullet">
/// <item>有效版本已稳定，或非正常退出次数未达上限：照常激活。</item>
/// <item>达到上限：上一生效版本曾经稳定运行时，自动回滚到它一次（生成新版本，操作人为“系统”）；
/// 上一版本从未稳定、没有上一版本，或有效版本本身就是自动回滚生成的，进入“运行失败”。</item>
/// </list>
/// 回滚还须通过总设计 §8.3 的目标变更检查（T1-06、T3-06）；本批还没有待发记录的目标检查。
/// </summary>
internal static class StartupProtection
{
    /// <summary>从未稳定的有效版本累计 3 次非正常退出即触发保护（总设计 §8.4）。</summary>
    public const int AbnormalExitLimit = 3;

    public static StartupProtectionDecision Decide(RuntimeSnapshotRecord effective, RuntimeSnapshotRecord? previous)
    {
        ArgumentNullException.ThrowIfNull(effective);
        if (effective.IsStable || effective.AbnormalExitCount < AbnormalExitLimit)
        {
            return StartupProtectionDecision.Activate;
        }

        var exits = $"版本 r{effective.Revision} 从未稳定运行，已连续 {effective.AbnormalExitCount} 次非正常退出";
        if (effective.Origin == RuntimeSnapshotOrigins.AutoRollback)
        {
            return StartupProtectionDecision.Fail($"{exits}；它是启动保护自动回滚生成的版本，不再回滚。");
        }

        if (previous is null)
        {
            return StartupProtectionDecision.Fail($"{exits}；没有上一生效版本可以回滚。");
        }

        if (!previous.IsStable)
        {
            return StartupProtectionDecision.Fail($"{exits}；上一生效版本 r{previous.Revision} 从未稳定运行，不自动回滚。");
        }

        return StartupProtectionDecision.RollBackTo(previous.Revision, $"{exits}，自动回滚到上一生效版本 r{previous.Revision} 的内容。");
    }
}

/// <summary>启动保护的判定结果。</summary>
internal sealed record StartupProtectionDecision(StartupProtectionAction Action, long? RollbackTarget, string? Reason)
{
    public static StartupProtectionDecision Activate { get; } = new(StartupProtectionAction.Activate, null, null);

    public static StartupProtectionDecision Fail(string reason) => new(StartupProtectionAction.RunFailed, null, reason);

    public static StartupProtectionDecision RollBackTo(long revision, string reason) => new(StartupProtectionAction.RollBack, revision, reason);
}

internal enum StartupProtectionAction
{
    Activate,
    RollBack,
    RunFailed,
}
