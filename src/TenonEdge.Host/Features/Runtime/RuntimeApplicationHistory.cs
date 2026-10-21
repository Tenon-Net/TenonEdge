using TenonEdge.Host.Features.Configuration;
using TenonEdge.Host.Features.Routes;

namespace TenonEdge.Host.Features.Runtime;

internal enum RuntimeApplicationStage { Validating, Preparing, Quiescing, Committing, Activating, Restoring, Completed, Failed }

internal sealed record RuntimeApplicationStatus(Guid Id, string Kind, string Account, string DraftVersion, long? TargetRevision,
    long? Revision, RuntimeApplicationStage Stage, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt,
    bool? Committed, RuntimeApplicationResult? Result, string? ErrorCode);

/// <summary>
/// 有界的脱敏操作进度，不保存请求、异常对象或草稿正文。
/// 历史状态的读写由 RuntimeCoordinator 在同一个准入锁内完成；不另设锁、不调度后台任务，
/// 因此状态发布与应用/迁移/停机所有权保持原子性。进程重启后清空，不替代持久版本历史。
/// </summary>
internal sealed class RuntimeApplicationHistory(TimeProvider time)
{
    private const int MaximumRecentApplications = 20;
    private readonly List<RuntimeApplicationStatus> _items = [];

    public RuntimeApplicationStatus? Latest => _items.Count == 0 ? null : _items[^1];
    public RuntimeApplicationStatus? Read(Guid id) => _items.Find(application => application.Id == id);
    public RuntimeApplicationStatus[] Snapshot() => _items.AsEnumerable().Reverse().ToArray();

    public RuntimeApplicationStatus Start(Guid id, string kind, string account, string draftVersion, long? targetRevision)
    {
        if (_items.Any(application => application.Id == id))
        { throw new RuntimeApplicationUnavailableException(); }
        var initial = new RuntimeApplicationStatus(id, kind, account, draftVersion, targetRevision,
            null, RuntimeApplicationStage.Validating, time.GetUtcNow(), null, false, null, null);
        _items.Add(initial);
        if (_items.Count > MaximumRecentApplications)
        { _items.RemoveAt(0); }
        return initial;
    }

    public void Advance(Guid id, RuntimeApplicationStage stage, long revision, bool? committed) =>
        _items[^1] = Current(id) with { Stage = stage, Revision = revision, Committed = committed };

    public void Complete(Guid id, RuntimeApplicationResult result) =>
        _items[^1] = Current(id) with
        {
            Stage = result.Activated ? RuntimeApplicationStage.Completed : RuntimeApplicationStage.Failed,
            FinishedAt = time.GetUtcNow(),
            Committed = result.Committed,
            Result = result,
            ErrorCode = result.Activated ? null : result.Code,
        };

    public void Fail(Guid id, Exception exception) =>
        _items[^1] = Current(id) with
        { Stage = RuntimeApplicationStage.Failed, FinishedAt = time.GetUtcNow(), ErrorCode = ApplicationErrorCode(exception) };

    private RuntimeApplicationStatus Current(Guid id)
    {
        if (Latest is not { } current || current.Id != id)
        { throw new InvalidOperationException("操作不再持有应用所有权。"); }
        return current;
    }

    internal static string ApplicationErrorCode(Exception exception) => exception switch
    {
        DraftWriteException draft => draft.Code,
        RuntimeVersionConflictException version => version.Code,
        RouteTopologyException => "configuration.invalidRoute",
        RuntimePlanDocumentException => "configuration.invalidSnapshot",
        KeyNotFoundException => "configuration.versionNotFound",
        NotSupportedException => "configuration.unsupportedSnapshot",
        ArgumentException => "configuration.invalidRequest",
        _ => "runtime.applicationFailed",
    };
}
