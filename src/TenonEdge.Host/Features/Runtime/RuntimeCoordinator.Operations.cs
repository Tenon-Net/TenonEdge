using System.Runtime.ExceptionServices;
using TenonEdge.Host.Features.Configuration;
using TenonEdge.Host.Features.Routes;

namespace TenonEdge.Host.Features.Runtime;

internal enum RuntimeApplicationStage { Validating, Preparing, Quiescing, Committing, Activating, Restoring, Completed, Failed }

internal sealed record RuntimeApplicationStatus(Guid Id, string Kind, string Account, string DraftVersion, long? TargetRevision,
    long? Revision, RuntimeApplicationStage Stage, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt,
    bool? Committed, RuntimeApplicationResult? Result, string? ErrorCode);

internal sealed partial class RuntimeCoordinator
{
    private const int MaximumRecentApplications = 20;
    private readonly List<RuntimeApplicationStatus> _applications = [];

    /// <summary>最近一次操作（可能已结束）；只含脱敏进度，不含请求、异常对象或草稿内容。</summary>
    public RuntimeApplicationStatus? ApplicationStatus
    {
        get { lock (_lock) { return _applications.Count == 0 ? null : _applications[^1]; } }
    }

    public RuntimeApplicationStatus? ReadApplication(Guid id)
    {
        lock (_lock)
        { return _applications.Find(application => application.Id == id); }
    }

    public RuntimeApplicationStatus[] ReadApplications()
    {
        lock (_lock)
        { return _applications.AsEnumerable().Reverse().ToArray(); }
    }

    // 拥有的任务把异常转换为内部结果，不因请求取消而留下无人观察的故障任务。
    // 最近操作列表只保留脱敏状态，原异常只交给仍在等待的调用方。
    private async Task<ApplicationOutcome> RunApplicationAsync(long? targetRevision, string expectedDraftVersion,
        string account, string? sourceIp, Guid operationId, TaskCompletionSource completion, string? changeDescription, RuntimeApplicationExpectation? expectation)
    {
        try
        {
            var result = await ExecuteApplicationAsync(targetRevision, expectedDraftVersion, account, sourceIp, operationId, changeDescription, expectation);
            lock (_lock)
            {
                var current = _applications[^1];
                _applications[^1] = current with
                {
                    Stage = result.Activated ? RuntimeApplicationStage.Completed : RuntimeApplicationStage.Failed,
                    FinishedAt = time.GetUtcNow(),
                    Committed = result.Committed,
                    Result = result,
                    ErrorCode = result.Activated ? null : result.Code,
                };
            }
            return new(result, null);
        }
        catch (Exception exception)
        {
            lock (_lock)
            {
                _applications[^1] = _applications[^1] with
                { Stage = RuntimeApplicationStage.Failed, FinishedAt = time.GetUtcNow(), ErrorCode = ApplicationErrorCode(exception) };
            }
            return new(null, ExceptionDispatchInfo.Capture(exception));
        }
        finally
        {
            lock (_lock)
            {
                _application = null;
                completion.TrySetResult();
            }
        }
    }

    private static async Task<RuntimeApplicationResult> AwaitApplicationAsync(Task<ApplicationOutcome> owned, CancellationToken cancellationToken)
    {
        var outcome = await owned.WaitAsync(cancellationToken);
        outcome.Error?.Throw();
        return outcome.Result!;
    }

    private void AdvanceApplication(Guid id, RuntimeApplicationStage stage, long revision, bool? committed = false)
    {
        lock (_lock)
        {
            if (_applications[^1].Id != id)
            { throw new InvalidOperationException("操作不再持有应用所有权。"); }
            _applications[^1] = _applications[^1] with { Stage = stage, Revision = revision, Committed = committed };
        }
    }

    private static string ApplicationErrorCode(Exception exception) => exception switch
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

    private sealed record ApplicationOutcome(RuntimeApplicationResult? Result, ExceptionDispatchInfo? Error);
}
