namespace TenonEdge.Abstractions.Contracts;

/// <summary>发送者读取持久手动暂停及恢复代次；提供者不向运行时暴露配置库类型。</summary>
public interface IOutputControl
{
    OutputControlState Read(long connectionId);
}

public sealed record OutputControlState(bool ManuallyPaused, string? ResumeVersion);
