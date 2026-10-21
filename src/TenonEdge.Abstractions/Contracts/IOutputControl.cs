namespace TenonEdge.Abstractions.Contracts;

/// <summary>发送者读取持久手动暂停及恢复代次；提供者不向运行时暴露配置库类型。</summary>
public interface IOutputControl
{
    OutputControlState Read(long connectionId);
    /// <summary>可选的发送者独占实时查询；读取间隙不保留事务，循环结束释放。默认沿用普通查询。</summary>
    IOutputControlReadSession? OpenSenderReadSession() => null;
}

public interface IOutputControlReadSession : IOutputControl, IDisposable;

public sealed record OutputControlState(bool ManuallyPaused, string? ResumeVersion);
