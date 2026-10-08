namespace TenonEdge.Abstractions.Contracts;

/// <summary>
/// 运行事件的写入入口（总设计 §3.3，功能需求 §7.4）：运行时只记状态变化，由宿主保存、合并与清理。
/// 调用不得因存储失败而抛出异常；说明中不得包含报文正文、点位的值和秘密。
/// </summary>
public interface IRuntimeEventSink
{
    /// <param name="type">稳定的事件类型代码，例如 runtime.unitFaulted；发布后不得修改。</param>
    /// <param name="target">事件对象，例如运行单元标识或 deviceKey；没有时为空。</param>
    void Record(RuntimeEventSeverity severity, string type, string? target, string description);
}

/// <summary>运行事件级别。</summary>
public enum RuntimeEventSeverity
{
    Info,
    Warning,
    Error,
}
