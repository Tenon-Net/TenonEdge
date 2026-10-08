using TenonEdge.Abstractions.Contracts;

namespace TenonEdge.Host.Features.Runtime;

/// <summary>运行时事件写入配置库中的运行事件（同一对象的同类事件按 <see cref="RuntimeEventLog.MergeWindow"/> 合并；写入失败改写到日志）。</summary>
internal sealed class RuntimeEventSink(RuntimeEventLog log) : IRuntimeEventSink
{
    public void Record(RuntimeEventSeverity severity, string type, string? target, string description) =>
        log.Record(
            severity switch
            {
                RuntimeEventSeverity.Info => RuntimeEventLevel.Info,
                RuntimeEventSeverity.Warning => RuntimeEventLevel.Warning,
                _ => RuntimeEventLevel.Error,
            },
            type,
            target,
            description);
}
