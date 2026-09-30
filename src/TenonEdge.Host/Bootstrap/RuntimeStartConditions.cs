using System.Collections.Concurrent;

namespace TenonEdge.Host.Bootstrap;

/// <summary>
/// 运行时的启动条件（总设计 §2.3、§10.3）。以下情况下运行时不得启动，管理界面显示原因：
/// 实际加载的 SQLite 低于 3.51.3；在容器中运行而数据目录不在已挂载的卷上（T0-06）。
/// 运行时宿主在 T1-01 建立，这里先登记阻止原因：就绪检查据此返回 503，原因写入日志；
/// 运行时宿主和系统信息接口读取的也是这一份结果。
/// </summary>
internal sealed class RuntimeStartConditions
{
    private readonly ConcurrentDictionary<string, RuntimeStartBlocker> _blockers = new(StringComparer.Ordinal);

    public bool AllowsRuntimeStart => _blockers.IsEmpty;

    public IReadOnlyList<RuntimeStartBlocker> Blockers =>
        [.. _blockers.Values.OrderBy(blocker => blocker.Code, StringComparer.Ordinal)];

    /// <summary>登记一项阻止原因；同一代码重复登记时以最后一次为准。</summary>
    public void Block(string code, string reason) => _blockers[code] = new RuntimeStartBlocker(code, reason);
}

/// <summary>一项阻止运行时启动的原因。代码稳定、供程序判断，说明面向管理员。</summary>
internal sealed record RuntimeStartBlocker(string Code, string Reason);
