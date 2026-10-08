using TenonEdge.Abstractions.Plans;
using TenonEdge.Runtime.Diagnostics;

namespace TenonEdge.Runtime.Lifecycle;

/// <summary>一个运行单元的状态快照，供状态查询与测试使用；最后错误只含说明，不含堆栈。</summary>
public sealed record RuntimeUnitStatus(
    RuntimeUnitId Id,
    RuntimeUnitState State,
    int RestartCount,
    string? LastError,
    DateTimeOffset? LastErrorAt,
    DateTimeOffset? NextRestartAt,
    InputReadSnapshot? Reads);

/// <summary>运行单元的生命周期状态（总设计 §7.4、§8.1）。</summary>
public enum RuntimeUnitState
{
    /// <summary>循环正在运行。</summary>
    Running,

    /// <summary>上一次运行异常结束，正在等待退避后重启。</summary>
    Backoff,

    /// <summary>已请求停止，等待循环退出。</summary>
    Stopping,

    /// <summary>循环已退出。</summary>
    Stopped,

    /// <summary>停止期限内循环没有退出：单元仍视为存活，同一标识不创建新实例，需重启服务（总设计 §7.4 单实例规则）。</summary>
    StopTimedOut,

    /// <summary>构建失败（结构性问题），没有启动。</summary>
    BuildFailed,
}

/// <summary>运行时的状态快照。</summary>
public sealed record RuntimeStatusReport(
    long? ActiveRevision,
    IReadOnlyList<RuntimeUnitStatus> Units);
